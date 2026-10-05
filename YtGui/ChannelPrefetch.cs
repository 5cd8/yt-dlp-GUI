using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace YtGui
{
    internal sealed record ChannelRef(SiteKind Kind, string BaseUrl, string Name);

    internal sealed record ArchiveRef(SiteKind Kind, string Id, string Url, string Title);

    internal sealed record ChatFetchOutcome(string? ChatPath, string Detail);

    internal sealed record ChannelPrefetchResult(
        int ChannelCount,
        int FailedChannelCount,
        int ArchiveCount,
        int SkippedArchiveCount,
        int FailedArchiveCount,
        int DownloadedCount,
        int FailedUrlCount,
        bool IsCanceled);

    internal interface IChannelPrefetchTools
    {
        // 実行できない理由（Twitchチャット取得ツールが未設定など）。実行できるなら null。
        string? GetUnavailableReason(SiteKind kind);
        Task<IReadOnlyList<ArchiveRef>> ListArchivesAsync(ChannelRef channel, int count, CancellationToken token);
        // 取得できたチャットリプレイのパスを返す。取得できなければ ChatPath が null で、Detail にログへ出す理由。
        Task<ChatFetchOutcome> FetchChatReplayAsync(ArchiveRef archive, string workDirectory, CancellationToken token);
    }

    // 一覧の行を順に受け取り、count 件に達したら以降を読み捨てる。yt-dlp を止める役目は持たない。
    internal sealed class ArchiveListingCollector
    {
        readonly SiteKind kind;
        readonly int count;

        public ArchiveListingCollector(SiteKind kind, int count)
        {
            this.kind = kind;
            this.count = count;
        }

        public List<ArchiveRef> Archives { get; } = new();

        public bool IsFull => Archives.Count >= count;

        public void AddLine(string line)
        {
            if (IsFull) return;
            var archive = ChannelPrefetch.TryParseArchiveLine(kind, line);
            if (archive != null) Archives.Add(archive);
        }
    }

    internal sealed class ProcessedArchiveStore
    {
        readonly string path;
        readonly HashSet<string> keys;

        ProcessedArchiveStore(string path, HashSet<string> keys)
        {
            this.path = path;
            this.keys = keys;
        }

        public static ProcessedArchiveStore Load(string path, Action<string> log)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                // File.Exists で先に確かめない。パスがディレクトリのとき false を返し、異常が見えなくなるため。
                foreach (var line in File.ReadAllLines(path))
                    if (line.Length > 0) keys.Add(line);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"処理済みの記録を読めませんでした（すべて未処理として扱います）: {path} ({ex.Message})");
            }
            return new ProcessedArchiveStore(path, keys);
        }

        static string BuildKey(SiteKind kind, string id) => kind + ":" + id;

        public bool Contains(SiteKind kind, string id) => keys.Contains(BuildKey(kind, id));

        // メモリに入れてからファイルへ追記する。書き込みに失敗しても、メモリの分は残る。
        public void Add(SiteKind kind, string id)
        {
            var key = BuildKey(kind, id);
            keys.Add(key);
            File.AppendAllText(path, key + "\n", new UTF8Encoding(false));
        }
    }

    internal static class ChannelPrefetch
    {
        // --playlist-end N だけだと、先頭に並ぶ配信中・配信予定の枠が数に入り、was_live がN件に足りなくなる。
        // 一覧は強制終了で止めると %TEMP%\_MEI* が毎回残るので、余裕を足した --playlist-end で自然終了させる。
        public const int ListingMargin = 50;

        static readonly Regex TwitchLoginPattern = new("^[A-Za-z0-9_]{1,25}$", RegexOptions.CultureInvariant);
        static readonly Regex TwitchVideoIdPattern = new("^v([0-9]+)$", RegexOptions.CultureInvariant);

        public static List<string> SplitChannelLines(string text)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (seen.Add(line)) result.Add(line);
            }
            return result;
        }

        public static bool HasChannelLines(string text) => SplitChannelLines(text).Count > 0;

        public static bool TryParseChannel(string line, [NotNullWhen(true)] out ChannelRef? channel)
        {
            channel = null;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            var kind = YtDlp.DetermineSiteKind(uri.AbsoluteUri);
            if (kind == SiteKind.Unsupported) return false;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            if (kind == SiteKind.YouTube)
            {
                if (segments[0].StartsWith('@') && segments[0].Length >= 2)
                {
                    // BaseUrl はエンコードのまま使い、ログの表示用の Name だけ元に戻す。
                    channel = new ChannelRef(kind, "https://www.youtube.com/" + segments[0], Uri.UnescapeDataString(segments[0]));
                    return true;
                }
                if (segments.Length >= 2
                    && segments[0].Equals("channel", StringComparison.OrdinalIgnoreCase)
                    && segments[1].StartsWith("UC", StringComparison.Ordinal))
                {
                    channel = new ChannelRef(kind, "https://www.youtube.com/channel/" + segments[1], segments[1]);
                    return true;
                }
                return false;
            }

            var login = segments[0];
            if (!TwitchLoginPattern.IsMatch(login)) return false;
            if (login.Equals("videos", StringComparison.OrdinalIgnoreCase)
                || login.Equals("directory", StringComparison.OrdinalIgnoreCase)) return false;
            channel = new ChannelRef(kind, "https://www.twitch.tv/" + login, login);
            return true;
        }

        public static ArchiveRef? TryParseArchiveLine(SiteKind kind, string line)
        {
            var parts = line.Split('\t', 3);
            if (parts.Length < 3) return null;
            var rawId = parts[0];
            var liveStatus = parts[1];
            var title = parts[2];

            if (kind == SiteKind.YouTube)
            {
                // 配信中の枠も is_upcoming などと出るので、was_live 以外はすべて飛ばす。
                if (liveStatus != "was_live" || rawId.Length == 0) return null;
                return new ArchiveRef(kind, rawId, "https://www.youtube.com/watch?v=" + rawId, title.Length == 0 ? rawId : title);
            }
            if (kind == SiteKind.Twitch)
            {
                var match = TwitchVideoIdPattern.Match(rawId);
                if (!match.Success) return null;
                var id = match.Groups[1].Value;
                return new ArchiveRef(kind, id, "https://www.twitch.tv/videos/" + id, title.Length == 0 ? id : title);
            }
            return null;
        }

        // 前提：count >= 1。
        public static async Task<ChannelPrefetchResult> RunAsync(
            string channelUrlsText, int count, string cacheDirectory, string workRoot,
            ProcessedArchiveStore store, IChannelPrefetchTools tools, Action<string> log, CancellationToken token)
        {
            var lines = SplitChannelLines(channelUrlsText);
            int failedChannels = 0, archiveTotal = 0, skipped = 0, failedArchives = 0, downloaded = 0, failedUrls = 0;
            ChannelPrefetchResult Build(bool canceled) =>
                new(lines.Count, failedChannels, archiveTotal, skipped, failedArchives, downloaded, failedUrls, canceled);

            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var line = lines[i];
                    var prefix = $"チャンネル事前投入 ({i + 1}/{lines.Count})";

                    if (!TryParseChannel(line, out var channel))
                    {
                        log($"{prefix}: チャンネルURLを解釈できないため飛ばしました: {line}");
                        failedChannels++;
                        continue;
                    }
                    var unavailableReason = tools.GetUnavailableReason(channel.Kind);
                    if (unavailableReason != null)
                    {
                        log($"{prefix}: {unavailableReason}ため飛ばしました: {channel.Name}");
                        failedChannels++;
                        continue;
                    }

                    IReadOnlyList<ArchiveRef> archives;
                    try
                    {
                        archives = await tools.ListArchivesAsync(channel, count, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log($"{prefix}: アーカイブ一覧の取得に失敗しました: {channel.Name} ({ex.Message})");
                        failedChannels++;
                        continue;
                    }
                    if (archives.Count == 0)
                        log($"{prefix}: 対象のアーカイブが見つかりませんでした: {channel.Name}");
                    archiveTotal += archives.Count;

                    for (int j = 0; j < archives.Count; j++)
                    {
                        token.ThrowIfCancellationRequested();
                        var archive = archives[j];
                        // 処理済みはログを出さない（2回目以降にログ欄を埋めないため）。
                        if (store.Contains(archive.Kind, archive.Id))
                        {
                            skipped++;
                            continue;
                        }
                        var archivePrefix = $"{prefix} [{j + 1}/{archives.Count}]:";
                        log($"{archivePrefix} チャットリプレイを取得します: {archive.Title} ({archive.Id})");

                        var workDirectory = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
                        try
                        {
                            Directory.CreateDirectory(workDirectory);
                            var outcome = await tools.FetchChatReplayAsync(archive, workDirectory, token);
                            if (outcome.ChatPath == null)
                            {
                                log($"{archivePrefix} チャットリプレイを取得できませんでした: {archive.Title} {outcome.Detail}");
                                failedArchives++;
                                continue;
                            }
                            var (a, b) = await EmojiCache.PopulateAsync(outcome.ChatPath, archive.Kind, cacheDirectory, log, token);
                            downloaded += a;
                            failedUrls += b;
                            // 絵文字の失敗件数に関わらず記録する。PopulateAsync はキャッシュに無い新規分だけを取るので、
                            // 失敗で未処理に戻すと、404の絵文字を持つアーカイブが毎回チャット全体を取り直される。
                            try { store.Add(archive.Kind, archive.Id); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                log($"{archivePrefix} 処理済みの記録を書き込めませんでした（次回、このアーカイブをもう一度取得します）: {archive.Id} ({ex.Message})");
                            }
                            log($"{archivePrefix} 投入しました: 取得 {a}件, 失敗 {b}件");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            log($"{archivePrefix} 処理に失敗しました: {archive.Title} ({ex.Message})");
                            failedArchives++;
                        }
                        finally
                        {
                            try { Directory.Delete(workDirectory, recursive: true); }
                            catch (DirectoryNotFoundException) { }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                log($"{archivePrefix} 一時フォルダを削除できませんでした: {workDirectory} ({ex.Message})");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Build(true);
            }
            return Build(false);
        }
    }
}
