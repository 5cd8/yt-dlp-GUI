using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtGui
{
    // StartFailed は起動に失敗した（exe が無い・実行できない）こと、TimedOut は時間切れで止めたこと。どちらも ExitCode は null。
    internal readonly record struct ProcessRunResult(int? ExitCode, string Output, bool StartFailed, bool TimedOut);

    // 外部ツールの更新で使う、ダウンロード・ハッシュ検証・zip の展開・入れ替え（巻き戻し付き）・外部プロセスの実行（docs/adr/0009参照）。
    // Settings に依存しない。await には ConfigureAwait(false) を付ける（MainForm_FormClosing が UI スレッドから Wait で待つため）。
    internal static class ToolInstaller
    {
        const int BufferSize = 81920;
        static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(60);
        static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);
        static readonly int[] ReportedPercents = { 25, 50, 75, 100 };

        // GitHub API の応答を JSON で返す。呼び出し側が using var で持ち、RootElement を使い終わるまで破棄しない。
        public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(ApiTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonDocument.Parse(text);
        }

        // 25・50・75・100 のうち、ダウンロード済みの割合が達した最大のしきい値が lastReported より大きければそれを返す。無ければ null。
        public static int? NextReportedPercent(long downloaded, long total, int lastReported)
        {
            if (total <= 0 || downloaded < 0) return null;
            var percent = downloaded * 100 / total;
            int? reached = null;
            foreach (var step in ReportedPercents)
                if (percent >= step) reached = step;
            return reached > lastReported ? reached : null;
        }

        public static async Task DownloadAsync(HttpClient http, string url, string destinationPath, long expectedSize, Action<int> reportPercent, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(DownloadTimeout);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long downloaded = 0;
            var lastReported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                downloaded += read;
                var step = NextReportedPercent(downloaded, expectedSize, lastReported);
                if (step != null)
                {
                    lastReported = step.Value;
                    reportPercent(lastReported);
                }
            }
        }

        public static async Task<bool> VerifySha256Async(string path, string expectedHex, CancellationToken token)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        // 書き出し先は呼び出し側が決めた固定のパスだけ。エントリ名は書き出し先のパスに使わない（zip-slip を避ける）。
        public static async Task ExtractFilesAsync(string zipPath, IReadOnlyList<(Regex EntryName, string DestinationPath)> files, CancellationToken token)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var (entryName, destinationPath) in files)
            {
                var entry = zip.Entries.FirstOrDefault(e => entryName.IsMatch(e.FullName))
                    ?? throw new FileNotFoundException("zip に必要なファイルがありません: " + entryName);
                await using var source = entry.Open();
                await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await source.CopyToAsync(destination, BufferSize, token).ConfigureAwait(false);
            }
        }

        // 旧版は「<名前>.old」として1世代だけ残す（前回の .old は上書きされる）。どこかで失敗したら、全部を元に戻して例外を投げ直す。
        // 同期で、中止を受け付けない（途中で止めると、ffmpeg.exe が無い状態になるため）。呼び出し側が直前に中止を確かめる。
        public static void SwapWithRollback(IReadOnlyList<(string StagedPath, string TargetPath)> items)
        {
            var done = new List<(string Target, string Old, bool TargetExisted)>();
            try
            {
                foreach (var (staged, target) in items)
                {
                    var old = target + ".old";
                    var targetExisted = File.Exists(target);
                    if (targetExisted) File.Move(target, old, overwrite: true);
                    // 元のファイルが無かった項目も、後の項目の失敗で、置いた新しいファイルを消すために一覧へ入れる。
                    done.Add((target, old, targetExisted));
                    File.Move(staged, target);
                }
            }
            catch
            {
                for (var i = done.Count - 1; i >= 0; i--)
                {
                    var (target, old, targetExisted) = done[i];
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    try { if (targetExisted) File.Move(old, target, overwrite: true); } catch { }
                }
                throw;
            }
        }

        // PATH を ; で分け、前後の空白と " を除き、空の要素は飛ばして、fileName が最初に見つかったパスを返す。
        // Process.Start("deno") のように名前だけで起動すると、Windows はアプリのフォルダを先に探し、PATH の先頭と違うものが選ばれうる。
        public static string? FindOnPath(string fileName, string? pathVariable)
        {
            if (string.IsNullOrWhiteSpace(pathVariable)) return null;
            foreach (var part in pathVariable.Split(';'))
            {
                var directory = part.Trim().Trim('"').Trim();
                if (directory.Length == 0) continue;
                try
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // 不正な文字を含む要素は飛ばす。
                }
            }
            return null;
        }

        // 起動の失敗と時間切れは例外にせず、結果で返す。アプリの終了（外側の token）のときだけ OperationCanceledException を投げる。
        // 標準出力と標準エラーは両方とも最後まで読み、あわせて Output に入れる。
        public static async Task<ProcessRunResult> RunProcessAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
        {
            token.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            if (environment != null)
                foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;

            // 2つのイベントは別のスレッドで発火するので、追記は lock する。
            var output = new StringBuilder();
            var gate = new object();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
            string Snapshot() { lock (gate) return output.ToString(); }

            try
            {
                proc.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return new ProcessRunResult(null, string.Empty, true, false);
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            try
            {
                // WaitForExitAsync は、非同期で読んでいる標準出力・標準エラーの読み切りまで待つ。起動の直後に中止されていても、ここですぐ中止される。
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                return new ProcessRunResult(proc.ExitCode, Snapshot(), false, false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                // exe のロックが残ったまま更新用フォルダを消して失敗しないよう、終了を少し待つ。
                using var waitCts = new CancellationTokenSource(KillWait);
                try { await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                if (token.IsCancellationRequested) throw;
                return new ProcessRunResult(null, Snapshot(), false, true);
            }
        }
    }
}
