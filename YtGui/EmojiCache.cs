using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YtGui
{
    internal static class EmojiCache
    {
        static readonly string[] RendererNames =
        {
            "liveChatTextMessageRenderer",
            "liveChatPaidMessageRenderer",
            "liveChatMembershipItemRenderer"
        };

        static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        public static List<string> ExtractYouTubeEmojiUrls(string liveChatJsonPath)
        {
            var urls = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(liveChatJsonPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("replayChatItemAction", out var replayAction)) continue;
                    if (!replayAction.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) continue;
                    var firstAction = actions.EnumerateArray().FirstOrDefault();
                    if (firstAction.ValueKind != JsonValueKind.Object) continue;
                    if (!firstAction.TryGetProperty("addChatItemAction", out var addItem)) continue;
                    if (!addItem.TryGetProperty("item", out var item)) continue;

                    foreach (var rendererName in RendererNames)
                    {
                        if (item.TryGetProperty(rendererName, out var renderer))
                            CollectYouTubeEmojiUrls(renderer, urls, seen);
                    }
                }
                catch (JsonException) { }
            }
            return urls;
        }

        static void CollectYouTubeEmojiUrls(JsonElement renderer, List<string> urls, HashSet<string> seen)
        {
            if (!renderer.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return;

            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("emoji", out var emoji)) continue;

                bool isCustom;
                if (emoji.TryGetProperty("isCustomEmoji", out var customFlag))
                    isCustom = customFlag.ValueKind == JsonValueKind.True;
                else
                    isCustom = emoji.TryGetProperty("image", out var legacyImage) && legacyImage.TryGetProperty("thumbnails", out _);
                if (!isCustom) continue;

                if (!emoji.TryGetProperty("image", out var image)) continue;
                if (!image.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Array) continue;
                var first = thumbs.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object) continue;
                if (!first.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) continue;

                var url = urlEl.GetString();
                if (!string.IsNullOrEmpty(url) && seen.Add(url)) urls.Add(url);
            }
        }

        public static List<string> ExtractTwitchEmoteUrls(string chatJsonPath)
        {
            var urls = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var stream = File.OpenRead(chatJsonPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return urls;

            foreach (var comment in comments.EnumerateArray())
            {
                if (!comment.TryGetProperty("message", out var message)) continue;
                if (!message.TryGetProperty("fragments", out var fragments) || fragments.ValueKind != JsonValueKind.Array) continue;

                foreach (var fragment in fragments.EnumerateArray())
                {
                    if (!fragment.TryGetProperty("emoticon", out var emoticon) || emoticon.ValueKind != JsonValueKind.Object) continue;
                    if (!emoticon.TryGetProperty("emoticon_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                    var id = idEl.GetString();
                    if (string.IsNullOrEmpty(id)) continue;

                    var url = $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0";
                    if (seen.Add(url)) urls.Add(url);
                }
            }
            return urls;
        }

        const int FormatProbeByteCount = 4096;

        // 先頭だけを見る。yt-dlpのlive_chat.jsonは1行が長く、Twitchの圧縮JSONは全体が1行なので、ReadLineでは全体を読んでしまう。
        // YouTubeを先に判定するのは、YouTubeのチャット本文に "comments" が含まれ得るため（replayChatItemAction は1行目の先頭付近に出る）。
        internal static SiteKind DetectChatSiteKind(ReadOnlySpan<byte> head)
        {
            if (head.IndexOf("replayChatItemAction"u8) >= 0) return SiteKind.YouTube;
            if (head.IndexOf("\"streamer\""u8) >= 0 || head.IndexOf("\"comments\""u8) >= 0) return SiteKind.Twitch;
            return SiteKind.Unsupported;
        }

        public static SiteKind DetectChatSiteKind(string path)
        {
            var buffer = new byte[FormatProbeByteCount];
            using var stream = File.OpenRead(path);
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return DetectChatSiteKind(buffer.AsSpan(0, read));
        }

        // AttributesToSkip の既定（Hidden | System）だと非表示ファイルが黙って対象外になるので、0にしてすべて対象にする。
        public static IEnumerable<string> EnumerateJsonFiles(string folder) =>
            Directory.EnumerateFiles(folder, "*.json", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            });

        public static List<(string FilePath, SiteKind Kind)> FindChatReplayFiles(string folder, Action<string> log, CancellationToken token)
        {
            var found = new List<(string FilePath, SiteKind Kind)>();
            foreach (var path in EnumerateJsonFiles(folder))
            {
                token.ThrowIfCancellationRequested();
                SiteKind kind;
                try
                {
                    kind = DetectChatSiteKind(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"読み込めなかったため飛ばしました: {path} ({ex.Message})");
                    continue;
                }
                if (kind != SiteKind.Unsupported) found.Add((path, kind));
            }
            found.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath));
            return found;
        }

        public sealed record FolderBulkResult(
            int TargetFileCount,
            int CompletedFileCount,
            int FailedFileCount,
            int DownloadedCount,
            int FailedUrlCount,
            bool IsCanceled);

        // ファイルごとに PopulateAsync を呼ぶのは、中止しても完了したファイルの分をDBに残すため。
        // 取得0件・失敗0件の行を出さないのは、2回目以降の実行でログ欄（2000行で切り詰め）が意味の無い行で埋まるため。
        public static async Task<FolderBulkResult> PopulateFilesAsync(
            IReadOnlyList<(string FilePath, SiteKind Kind)> files, string folder, string cacheDirectory,
            Action<string> log, CancellationToken token)
        {
            var total = files.Count;
            var completed = 0;
            var failedFiles = 0;
            var downloaded = 0;
            var failedUrls = 0;
            try
            {
                for (var i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var (filePath, kind) = files[i];
                    var relative = Path.GetRelativePath(folder, filePath);
                    try
                    {
                        var (a, b) = await PopulateAsync(filePath, kind, cacheDirectory, log, token).ConfigureAwait(false);
                        downloaded += a;
                        failedUrls += b;
                        completed++;
                        if (a != 0 || b != 0)
                            log($"フォルダ一括投入 ({i + 1}/{total}): {relative} — 取得 {a}件, 失敗 {b}件");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failedFiles++;
                        completed++;
                        log($"フォルダ一括投入 ({i + 1}/{total}): 処理に失敗しました: {relative} ({ex.Message})");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new FolderBulkResult(total, completed, failedFiles, downloaded, failedUrls, true);
            }
            return new FolderBulkResult(total, completed, failedFiles, downloaded, failedUrls, false);
        }

        public static async Task<(int Downloaded, int Failed)> PopulateAsync(
            string chatJsonPath, SiteKind siteKind, string outputDirectory, Action<string> log, CancellationToken token)
        {
            var urls = siteKind switch
            {
                SiteKind.YouTube => ExtractYouTubeEmojiUrls(chatJsonPath),
                SiteKind.Twitch => ExtractTwitchEmoteUrls(chatJsonPath),
                _ => new List<string>()
            };
            if (urls.Count == 0) return (0, 0);

            var dbPath = Path.Combine(outputDirectory, "emoji_cache.sqlite");
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA busy_timeout=5000;";
                pragmaCmd.ExecuteNonQuery();
            }
            using (var createCmd = connection.CreateCommand())
            {
                createCmd.CommandText = "CREATE TABLE IF NOT EXISTS emoji_cache (url TEXT PRIMARY KEY, data BLOB NOT NULL);";
                createCmd.ExecuteNonQuery();
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT url FROM emoji_cache";
                using var reader = selectCmd.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            var toFetch = urls.Where(u => !existing.Contains(u)).ToList();
            if (toFetch.Count == 0) return (0, 0);

            var failed = 0;
            var results = new System.Collections.Concurrent.ConcurrentBag<(string Url, byte[] Data)>();
            using var semaphore = new SemaphoreSlim(4);

            async Task<byte[]?> DownloadOneAsync(string url)
            {
                try
                {
                    using var response = await httpClient.GetAsync(url, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return null;
                    var data = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    return LooksLikeImage(data) ? data : null;
                }
                // HttpClient のタイムアウトも OperationCanceledException で届く（.NET 5以降は TimeoutException を内包。
                // https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.getasync の GetAsync(String, CancellationToken)）。
                // 無条件に再スローすると、1件のタイムアウトで WhenAll ごと失敗し、取得済みの分も書き込まれない。
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { return null; }
            }

            await Task.WhenAll(toFetch.Select(async url =>
            {
                await semaphore.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var data = await DownloadOneAsync(url).ConfigureAwait(false);
                    if (data != null) results.Add((url, data));
                    else
                    {
                        Interlocked.Increment(ref failed);
                        log($"絵文字画像のダウンロードに失敗しました: {url}");
                    }
                }
                finally { semaphore.Release(); }
            })).ConfigureAwait(false);

            var downloaded = 0;
            foreach (var (url, data) in results)
            {
                try
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.CommandText = "INSERT OR REPLACE INTO emoji_cache (url, data) VALUES ($url, $data)";
                    insertCmd.Parameters.AddWithValue("$url", url);
                    insertCmd.Parameters.AddWithValue("$data", data);
                    insertCmd.ExecuteNonQuery();
                    downloaded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log($"絵文字キャッシュへの書き込みに失敗しました: {url} ({ex.Message})");
                }
            }

            return (downloaded, failed);
        }

        static bool LooksLikeImage(byte[] data)
        {
            if (data.Length < 12) return false;
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true; // PNG
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true; // JPEG
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return true; // GIF
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return true; // WEBP
            return false;
        }
    }
}
