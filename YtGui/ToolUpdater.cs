using System.Text.RegularExpressions;

namespace YtGui
{
    // 起動時の外部ツールの更新（ffmpeg → deno → TwitchDownloaderCLI）。yt-dlp の更新（yt-dlp -U）は MainForm が行う（docs/adr/0009参照）。
    // 失敗は実行ログへの警告にとどめ、ステータスやキュー処理には触れない。await には ConfigureAwait(false) を付ける。
    internal sealed class ToolUpdater
    {
        const string StagingRootName = ".ytgui-update";
        const string ReleasesUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases?per_page=11";
        const string TwitchToolLatestUrl = "https://api.github.com/repos/lay295/TwitchDownloader/releases/latest";
        static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan DenoTimeout = TimeSpan.FromMinutes(10);
        static readonly string[] SharedDllPatterns =
        {
            "avcodec-*.dll", "avdevice-*.dll", "avfilter-*.dll", "avformat-*.dll", "avutil-*.dll", "postproc-*.dll", "swresample-*.dll", "swscale-*.dll",
        };

        // SettingsForm が同じ Settings オブジェクトを UI スレッドで書き換えるので、パスはコンストラクタで写し取る。
        readonly string ffmpegPath;
        readonly string twitchToolPath;
        readonly Action<string> log;
        readonly HttpClient http;

        public ToolUpdater(Settings settings, Action<string> log, HttpClient http)
        {
            ffmpegPath = settings.FfmpegPath;
            twitchToolPath = settings.TwitchChatToolPath;
            this.log = log;
            this.http = http;
        }

        // アプリの終了（token の中止）だけを投げ直す。CancelAfter による時間切れも OperationCanceledException で届くが、token は中止されていないので、ログに出して次へ進む。
        public async Task RunAsync(CancellationToken token)
        {
            await GuardAsync("ffmpeg", UpdateFfmpegAsync, token).ConfigureAwait(false);
            await GuardAsync("deno", UpdateDenoAsync, token).ConfigureAwait(false);
            await GuardAsync("TwitchDownloaderCLI", UpdateTwitchToolAsync, token).ConfigureAwait(false);
        }

        async Task GuardAsync(string name, Func<CancellationToken, Task> update, CancellationToken token)
        {
            try { await update(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { log($"{name}: タイムアウトしました。"); }
            catch (Exception ex) { log($"{name}: 更新でエラー: {ex.Message}"); }
        }

        // ---- ffmpeg ----

        async Task UpdateFfmpegAsync(CancellationToken token)
        {
            const string name = "ffmpeg";
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                log("ffmpeg: 設定にパスが無いので更新しません（PATH 上の ffmpeg を使います）。");
                return;
            }
            if (!File.Exists(ffmpegPath))
            {
                log("ffmpeg: ファイルが見つからないので更新しません: " + ffmpegPath);
                return;
            }
            if (!string.Equals(Path.GetFileName(ffmpegPath), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                log("ffmpeg: ファイル名が ffmpeg.exe ではないので更新しません: " + ffmpegPath);
                return;
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(ffmpegPath))!;
            var staging = Path.Combine(directory, StagingRootName, "ffmpeg");
            if (!TryRemoveStaging(staging, name)) return;
            try
            {
                var current = await RunProcessAsync(ffmpegPath, new[] { "-version" }, VersionTimeout, token).ConfigureAwait(false);
                Version? installed = null;
                if (!current.StartFailed && !current.TimedOut)
                    ToolVersions.TryParseFfmpegFamilyVersion(current.Output, "ffmpeg", out installed);

                ToolRelease? latest;
                using (var doc = await ToolInstaller.GetJsonAsync(http, ReleasesUrl, token).ConfigureAwait(false))
                    latest = ToolReleaseSources.SelectFfmpegRelease(doc.RootElement);
                if (latest == null)
                {
                    log("ffmpeg: 更新対象のビルドが見つかりませんでした。");
                    return;
                }
                if (!ToolVersions.IsUpdateNeeded(installed, latest.Version))
                {
                    log($"ffmpeg: 最新です ({installed})。");
                    return;
                }
                log($"ffmpeg: {(installed == null ? "不明" : installed.ToString())} → {latest.Version} に更新します（{latest.Size / 1024 / 1024}MB）...");

                if (!TryCreateStaging(staging, name)) return;
                var zipPath = Path.Combine(staging, latest.AssetName);
                await ToolInstaller.DownloadAsync(http, latest.DownloadUrl, zipPath, latest.Size, percent => log($"ffmpeg: ダウンロード中… {percent}%"), token).ConfigureAwait(false);
                if (!await ToolInstaller.VerifySha256Async(zipPath, latest.Sha256Hex, token).ConfigureAwait(false))
                {
                    log("ffmpeg: ハッシュが一致しないので更新しません。");
                    return;
                }

                var programs = new List<string> { "ffmpeg", "ffprobe" };
                if (File.Exists(Path.Combine(directory, "ffplay.exe"))) programs.Add("ffplay");
                var files = programs
                    .Select(p => (new Regex(@"^[^/]+/bin/" + p + @"\.exe$", RegexOptions.CultureInvariant), Path.Combine(staging, p + ".exe")))
                    .ToList();
                await ToolInstaller.ExtractFilesAsync(zipPath, files, token).ConfigureAwait(false);

                foreach (var program in programs)
                {
                    var check = await RunProcessAsync(Path.Combine(staging, program + ".exe"), new[] { "-version" }, VersionTimeout, token).ConfigureAwait(false);
                    var works = !check.StartFailed && !check.TimedOut && check.ExitCode == 0
                        && ToolVersions.TryParseFfmpegFamilyVersion(check.Output, program, out var stagedVersion) && stagedVersion == latest.Version;
                    if (!works)
                    {
                        log("ffmpeg: 新しい exe が動かないので更新しません。");
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();
                var items = programs.Select(p => (Path.Combine(staging, p + ".exe"), Path.Combine(directory, p + ".exe"))).ToList();
                try
                {
                    ToolInstaller.SwapWithRollback(items);
                }
                catch (Exception ex)
                {
                    log($"ffmpeg: 入れ替えに失敗しました（元に戻しています）: {ex.Message}");
                    return;
                }
                log($"ffmpeg: {latest.Version} に更新しました。");
                ReportSharedDlls(directory);
            }
            finally
            {
                RemoveStaging(directory, staging);
            }
        }

        // 入れ替えに成功した回だけ出す。消さない（Issue #44 の要件9）。
        void ReportSharedDlls(string directory)
        {
            var found = new List<string>();
            foreach (var pattern in SharedDllPatterns)
            {
                try { found.AddRange(Directory.GetFiles(directory, pattern).Select(Path.GetFileName).Where(n => n != null).Select(n => n!)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (found.Count > 0)
                log("ffmpeg: static ビルドになったので、不要になった DLL があります（削除して構いません）: " + string.Join("、", found.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
        }

        // ---- deno ----

        async Task UpdateDenoAsync(CancellationToken token)
        {
            // yt-dlp が使う deno は PATH の先頭にあるもの。WinGet 側など、後ろのものは対象外。
            var denoPath = ToolInstaller.FindOnPath("deno.exe", Environment.GetEnvironmentVariable("PATH"));
            if (denoPath == null)
            {
                log("deno: PATH に deno が無いので更新しません。");
                return;
            }
            var result = await RunProcessAsync(denoPath, new[] { "upgrade" }, DenoTimeout, token, new Dictionary<string, string> { ["NO_COLOR"] = "1" }).ConfigureAwait(false);
            if (result.StartFailed)
            {
                log("deno: 起動に失敗しました: " + denoPath);
                return;
            }
            foreach (var line in result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                log("deno: " + line);
            if (result.TimedOut) log("deno: タイムアウトしました。");
            else if (result.ExitCode != 0) log($"deno: 更新に失敗しました (exit {result.ExitCode})");
        }

        // ---- TwitchDownloaderCLI ----

        async Task UpdateTwitchToolAsync(CancellationToken token)
        {
            const string name = "TwitchDownloaderCLI";
            if (string.IsNullOrWhiteSpace(twitchToolPath) || !File.Exists(twitchToolPath))
            {
                log("TwitchDownloaderCLI: パスが未設定、またはファイルが無いので更新しません。");
                return;
            }
            if (!string.Equals(Path.GetFileName(twitchToolPath), "TwitchDownloaderCLI.exe", StringComparison.OrdinalIgnoreCase))
            {
                log("TwitchDownloaderCLI: ファイル名が TwitchDownloaderCLI.exe ではないので更新しません: " + twitchToolPath);
                return;
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(twitchToolPath))!;
            var staging = Path.Combine(directory, StagingRootName, "twitch");
            if (!TryRemoveStaging(staging, name)) return;
            try
            {
                // --version は、バージョンを標準エラーにだけ出し、終了コードは1を返す。終了コードは見ない。
                var current = await RunProcessAsync(twitchToolPath, new[] { "--version" }, VersionTimeout, token).ConfigureAwait(false);
                Version? installed = null;
                if (!current.StartFailed && !current.TimedOut)
                    ToolVersions.TryParseTwitchToolVersion(current.Output, out installed);

                ToolRelease? latest;
                using (var doc = await ToolInstaller.GetJsonAsync(http, TwitchToolLatestUrl, token).ConfigureAwait(false))
                    latest = ToolReleaseSources.SelectTwitchToolRelease(doc.RootElement);
                if (latest == null)
                {
                    log("TwitchDownloaderCLI: 更新対象のリリースが見つかりませんでした。");
                    return;
                }
                if (!ToolVersions.IsUpdateNeeded(installed, latest.Version))
                {
                    log($"TwitchDownloaderCLI: 最新です ({installed})。");
                    return;
                }
                log($"TwitchDownloaderCLI: {(installed == null ? "不明" : installed.ToString())} → {latest.Version} に更新します（{latest.Size / 1024 / 1024}MB）...");

                if (!TryCreateStaging(staging, name)) return;
                var zipPath = Path.Combine(staging, latest.AssetName);
                await ToolInstaller.DownloadAsync(http, latest.DownloadUrl, zipPath, latest.Size, percent => log($"TwitchDownloaderCLI: ダウンロード中… {percent}%"), token).ConfigureAwait(false);
                if (!await ToolInstaller.VerifySha256Async(zipPath, latest.Sha256Hex, token).ConfigureAwait(false))
                {
                    log("TwitchDownloaderCLI: ハッシュが一致しないので更新しません。");
                    return;
                }

                var stagedExe = Path.Combine(staging, "TwitchDownloaderCLI.exe");
                await ToolInstaller.ExtractFilesAsync(zipPath, new[] { (new Regex(@"^TwitchDownloaderCLI\.exe$", RegexOptions.CultureInvariant), stagedExe) }, token).ConfigureAwait(false);

                var check = await RunProcessAsync(stagedExe, new[] { "--version" }, VersionTimeout, token).ConfigureAwait(false);
                var works = !check.StartFailed && !check.TimedOut
                    && ToolVersions.TryParseTwitchToolVersion(check.Output, out var stagedVersion) && stagedVersion == latest.Version;
                if (!works)
                {
                    log("TwitchDownloaderCLI: 新しい exe が動かないので更新しません。");
                    return;
                }

                token.ThrowIfCancellationRequested();
                try
                {
                    ToolInstaller.SwapWithRollback(new[] { (stagedExe, twitchToolPath) });
                }
                catch (Exception ex)
                {
                    log($"TwitchDownloaderCLI: 入れ替えに失敗しました（元に戻しています）: {ex.Message}");
                    return;
                }
                log($"TwitchDownloaderCLI: {latest.Version} に更新しました。");
            }
            finally
            {
                RemoveStaging(directory, staging);
            }
        }

        // ---- 更新用フォルダ（<入れ替え先のフォルダ>\.ytgui-update\<ツール名>） ----

        static Task<ProcessRunResult> RunProcessAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
            => ToolInstaller.RunProcessAsync(exe, args, timeout, token, environment);

        // 前回のアプリが途中で終わった残りを、更新が要るかどうかに関わらず消す。消せなければ、同じフォルダを取り合わないよう、その項目を飛ばす。
        // .ytgui-update がディレクトリのときだけ扱う（ファイルのときは、作れなくて後で飛ばす）。
        bool TryRemoveStaging(string staging, string toolName)
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"{toolName}: 前回の更新用フォルダを消せないので更新しません: {ex.Message}");
                return false;
            }
        }

        bool TryCreateStaging(string staging, string toolName)
        {
            try
            {
                Directory.CreateDirectory(staging);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"{toolName}: 更新用フォルダを作れないので更新しません: {ex.Message}");
                return false;
            }
        }

        // 消せなくても無視する。.ytgui-update が空のディレクトリになったら、それも消す。
        static void RemoveStaging(string directory, string staging)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            try
            {
                var root = Path.Combine(directory, StagingRootName);
                if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
            catch { }
        }
    }
}
