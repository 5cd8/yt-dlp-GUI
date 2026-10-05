using System.Diagnostics;
using System.Text;

namespace YtGui
{
    // チャンネル事前投入で外部プロセス（yt-dlp・TwitchDownloaderCLI）を呼ぶ。
    // キュー項目の ActiveCts・ActiveProcPid には触らない（項目の中止・停止では止まらない）。
    internal sealed class ChannelPrefetchTools : IChannelPrefetchTools
    {
        readonly Settings settings;

        public ChannelPrefetchTools(Settings settings)
        {
            this.settings = settings;
        }

        public string? GetUnavailableReason(SiteKind kind)
            => kind == SiteKind.Twitch && string.IsNullOrWhiteSpace(settings.TwitchChatToolPath)
                ? "Twitchチャット取得ツールのパスが設定されていない"
                : null;

        public async Task<IReadOnlyList<ArchiveRef>> ListArchivesAsync(ChannelRef channel, int count, CancellationToken token)
        {
            var listUrl = channel.Kind == SiteKind.YouTube
                ? channel.BaseUrl + "/streams"
                : channel.BaseUrl + "/videos?filter=archives&sort=time";
            // yt-dlp は強制終了せず自然に終わらせる（理由は ListingMargin のコメント）。
            var end = channel.Kind == SiteKind.YouTube ? count + ChannelPrefetch.ListingMargin : count;
            var args = new List<string>
            {
                "--flat-playlist", "--playlist-end", end.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--no-warnings", "--encoding", "utf-8",
                "--print", "%(id)s\t%(live_status)s\t%(title)s", listUrl,
            };
            using var proc = new Process { StartInfo = YtDlp.CreateStartInfo(args, null, settings), EnableRaisingEvents = true };
            var collector = new ArchiveListingCollector(channel.Kind, count);
            var stderrBuffer = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (collector) collector.AddLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
            };

            await StartAndWaitAsync(proc, token);

            if (proc.ExitCode != 0)
            {
                string stderr;
                lock (stderrBuffer) stderr = stderrBuffer.ToString().Trim();
                throw new InvalidOperationException($"yt-dlp が終了コード {proc.ExitCode} で終了しました: {stderr}");
            }
            lock (collector) return collector.Archives.ToList();
        }

        public async Task<ChatFetchOutcome> FetchChatReplayAsync(ArchiveRef archive, string workDirectory, CancellationToken token)
        {
            if (archive.Kind == SiteKind.Twitch)
            {
                var twitchPath = Path.Combine(workDirectory, archive.Id + ".chat_replay.json");
                var psi = YtDlp.CreateTwitchChatToolStartInfo(new[] { "chatdownload", "--id", archive.Url, "-o", twitchPath }, settings);
                using var twitchProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var twitchStderr = new StringBuilder();
                twitchProc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (twitchStderr) twitchStderr.AppendLine(e.Data);
                };
                await StartAndWaitAsync(twitchProc, token);
                var twitchExit = twitchProc.ExitCode;
                if (twitchExit == 0 && File.Exists(twitchPath)) return new ChatFetchOutcome(twitchPath, "");
                string twitchDetail;
                lock (twitchStderr) twitchDetail = twitchStderr.ToString().Trim();
                return new ChatFetchOutcome(null, $"(exit {twitchExit}): {twitchDetail}");
            }

            var chatPath = Path.Combine(workDirectory, archive.Id + ".live_chat.json");
            using var proc = new Process
            {
                StartInfo = YtDlp.CreateStartInfo(LiveChatReplay.BuildDownloadArgs(archive.Url, chatPath), null, settings),
                EnableRaisingEvents = true,
            };
            var stderrBuffer = new StringBuilder();
            // 取れなかった理由（絞り込みで飛ばした、チャットが無い）は標準出力に出ることがある。進捗の行が大量に出うるので、最後の数行だけ残す。
            var stdoutTail = new Queue<string>();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stdoutTail)
                {
                    stdoutTail.Enqueue(e.Data);
                    if (stdoutTail.Count > 3) stdoutTail.Dequeue();
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
            };
            await StartAndWaitAsync(proc, token);
            // 取れたかどうかは終了コードではなくファイルの有無で決める（チャットリプレイがまだ無いと、終了コード0でファイルを作らない。ADR 0005）。
            // ここでは --skip-download で取ることが本番の経路。
            if (File.Exists(chatPath)) return new ChatFetchOutcome(chatPath, "");
            string output;
            lock (stderrBuffer) lock (stdoutTail) output = stderrBuffer + string.Join(Environment.NewLine, stdoutTail);
            return new ChatFetchOutcome(null, $"(exit {proc.ExitCode}): {output.Trim()}");
        }

        static async Task StartAndWaitAsync(Process proc, CancellationToken token)
        {
            using var reg = token.Register(() => KillQuietly(proc));
            // 中止済みのまま起動すると、起動した外部プロセスを止められずに残してしまう。
            token.ThrowIfCancellationRequested();
            proc.Start();
            if (proc.StartInfo.RedirectStandardOutput) proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            try
            {
                // WaitForExitAsync は、非同期で読んでいる標準出力・標準エラーの読み切りまで待つ。
                await proc.WaitForExitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 登録のKillは終了を待たずに戻る。終了を待たないと、プロセスが開いているファイルのせいで一時フォルダを消し残す。
                KillQuietly(proc);
                await proc.WaitForExitAsync();
                throw;
            }
        }

        static void KillQuietly(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }
}
