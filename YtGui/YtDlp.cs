using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtGui
{
    internal static class YtDlp
    {
        static readonly Regex LanguageInNote = new(@"\[([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})?)\]", RegexOptions.Compiled);

        public static (string Url, bool IsPlaylist) NormalizeUrl(string raw)
        {
            var text = raw.Trim();
            if (string.IsNullOrEmpty(text)) return (text, false);

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out uri))
                    return (text, false);
            }

            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];
            var youtube = host is "youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be"
                || host.EndsWith(".youtube.com");
            if (!youtube) return (uri.ToString(), false);

            var path = uri.AbsolutePath;
            var q = ParseQuery(uri.Query);

            if (host == "youtu.be")
            {
                var id = path.Trim('/');
                var slash = id.IndexOf('/');
                if (slash >= 0) id = id[..slash];
                if (!string.IsNullOrEmpty(id))
                    return ($"https://www.youtube.com/watch?v={id}", false);
            }

            if (path.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
            {
                if (q.TryGetValue("list", out var list) && !string.IsNullOrWhiteSpace(list))
                    return ($"https://www.youtube.com/playlist?list={list}", true);
                return (uri.ToString(), true);
            }

            if (TryPathId(path, "/shorts/", out var shortId))
                return ($"https://www.youtube.com/watch?v={shortId}", false);
            if (TryPathId(path, "/live/", out var liveId))
                return ($"https://www.youtube.com/watch?v={liveId}", false);

            if (q.TryGetValue("v", out var videoId) && !string.IsNullOrWhiteSpace(videoId))
                return ($"https://www.youtube.com/watch?v={videoId}", false);

            if (q.TryGetValue("list", out var listId) && !string.IsNullOrWhiteSpace(listId))
                return ($"https://www.youtube.com/playlist?list={listId}", true);

            return (uri.ToString(), false);
        }

        public static async Task<VideoInfo> GetVideoInfoAsync(string url, string? cookie, Settings settings)
        {
            var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8", "-o", "%(title)s.%(ext)s", url };
            var (exit, stdout, _) = await RunAsync(args, cookie, settings);
            if (exit == 0 && TryParseVideoJson(stdout, out var info) && info.Formats.Count > 0)
                return info;

            var listOutput = await GetFormatListAsync(url, cookie, settings);
            var formats = FormatListParser.Parse(listOutput);
            string title = string.Empty;
            try { title = await GetTitleAsync(url, cookie, settings); } catch { }
            return new VideoInfo
            {
                Title = title,
                SuggestedFileName = SuggestFileName(title, formats.FirstOrDefault()?.Ext ?? "mp4"),
                Formats = formats
            };
        }

        public static async Task<List<PlaylistEntry>> GetPlaylistEntriesAsync(string playlistUrl, string? cookie, Settings settings)
        {
            var args = new List<string> { "-J", "--flat-playlist", "--no-warnings", "--encoding", "utf-8", playlistUrl };
            var (exit, stdout, stderr) = await RunAsync(args, cookie, settings);
            if (exit != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException("yt-dlp がエラーを返しました: " + err);
            }

            var result = new List<PlaylistEntry>();
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (var el in entries.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var url = GetString(el, "url");
                    var id = GetString(el, "id");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        url = "https://www.youtube.com/watch?v=" + id;
                    }
                    else if (!url.Contains("://", StringComparison.Ordinal))
                    {
                        url = "https://www.youtube.com/watch?v=" + url;
                    }
                    result.Add(new PlaylistEntry(url, GetString(el, "title") ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("プレイリスト JSON の解析に失敗しました: " + ex.Message, ex);
            }
            return result;
        }

        public static string MakeUniquePath(string fullPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
                var name = Path.GetFileNameWithoutExtension(fullPath);
                var ext = Path.GetExtension(fullPath);
                var candidate = fullPath;
                int i = 1;
                while (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    candidate = Path.Combine(dir, $"{name}({i}){ext}");
                    i++;
                    if (i > 10000) break;
                }
                return candidate;
            }
            catch
            {
                return fullPath;
            }
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "video";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var cleaned = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "video" : cleaned;
        }

        public static string GuessExtension(string selectedFormat, IReadOnlyList<MediaFormat> formats, bool audioOnly, string codec)
        {
            if (audioOnly) return string.IsNullOrWhiteSpace(codec) ? "mp3" : codec;
            if (string.IsNullOrWhiteSpace(selectedFormat) || selectedFormat.Contains("best", StringComparison.OrdinalIgnoreCase))
                return "mp4";
            var parts = selectedFormat.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = new Dictionary<string, MediaFormat>(StringComparer.Ordinal);
            foreach (var f in formats)
            {
                if (!ids.ContainsKey(f.Id)) ids[f.Id] = f;
            }
            string? vExt = null, aExt = null;
            foreach (var part in parts)
            {
                if (!ids.TryGetValue(part, out var fmt)) continue;
                if (fmt.IsAudioOnly) aExt = fmt.Ext;
                else vExt = fmt.Ext;
            }
            if (!string.IsNullOrWhiteSpace(vExt) && !string.IsNullOrWhiteSpace(aExt) &&
                !string.Equals(vExt, aExt, StringComparison.OrdinalIgnoreCase))
                return "mkv";
            return vExt ?? aExt ?? "mp4";
        }

        public static ProcessStartInfo CreateStartInfo(IEnumerable<string> args, string? cookie, Settings current)
        {
            var exe = string.IsNullOrWhiteSpace(current.YtDlpPath) ? "yt-dlp" : current.YtDlpPath;
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var cookieToUse = cookie;
            if (string.IsNullOrWhiteSpace(cookieToUse) && !string.IsNullOrWhiteSpace(current.DefaultCookiePath))
                cookieToUse = current.DefaultCookiePath;
            if (!string.IsNullOrWhiteSpace(cookieToUse))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(cookieToUse);
            }
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            return psi;
        }

        public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IEnumerable<string> args, string? cookie, Settings settings)
        {
            var psi = CreateStartInfo(args, cookie, settings);
            using var proc = new Process { StartInfo = psi };
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("yt-dlp の実行に失敗しました: " + ex.Message, ex);
            }
            return (proc.ExitCode, sbOut.ToString(), sbErr.ToString());
        }

        static async Task<string> GetFormatListAsync(string url, string? cookie, Settings settings)
        {
            var (exit, stdout, stderr) = await RunAsync(new[] { "-F", "--no-playlist", "--encoding", "utf-8", url }, cookie, settings);
            if (exit != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException("yt-dlp -F がエラーを返しました: " + err);
            }
            return stdout;
        }

        public static async Task<string> GetTitleAsync(string url, string? cookie, Settings settings)
        {
            var (exit, stdout, stderr) = await RunAsync(new[] { "--get-title", "--no-playlist", "--encoding", "utf-8", url }, cookie, settings);
            if (exit != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException("yt-dlp --get-title がエラーを返しました: " + err);
            }
            var line = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return line.Length > 0 ? line[0] : string.Empty;
        }

        static bool TryParseVideoJson(string json, out VideoInfo info)
        {
            info = new VideoInfo();
            try
            {
                using var doc = JsonDocument.Parse(ExtractJsonObject(json));
                var root = doc.RootElement;
                var title = GetString(root, "title") ?? string.Empty;
                var filename = GetString(root, "_filename") ?? GetString(root, "filename") ?? string.Empty;
                var formats = new List<MediaFormat>();
                if (root.TryGetProperty("formats", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        var parsed = ParseFormatElement(el);
                        if (parsed != null) formats.Add(parsed);
                    }
                }
                if (formats.Count == 0) return false;
                if (string.IsNullOrWhiteSpace(filename))
                    filename = SuggestFileName(title, formats.First().Ext);
                info = new VideoInfo { Title = title, SuggestedFileName = Path.GetFileName(filename), Formats = formats };
                return true;
            }
            catch
            {
                return false;
            }
        }

        static MediaFormat? ParseFormatElement(JsonElement el)
        {
            var ext = GetString(el, "ext") ?? string.Empty;
            if (string.Equals(ext, "mhtml", StringComparison.OrdinalIgnoreCase)) return null;
            var id = GetString(el, "format_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith("sb", StringComparison.OrdinalIgnoreCase)) return null;
            var vcodec = GetString(el, "vcodec") ?? string.Empty;
            var acodec = GetString(el, "acodec") ?? string.Empty;
            var vNone = string.IsNullOrEmpty(vcodec) || vcodec == "none";
            var aNone = string.IsNullOrEmpty(acodec) || acodec == "none";
            if (vNone && aNone) return null;
            var note = (GetString(el, "format_note") ?? "") + " " + (GetString(el, "format") ?? "");
            var language = GetString(el, "language") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(language)) language = ExtractLanguage(note: note);
            var resolution = GetString(el, "resolution") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolution) || resolution.Contains("audio", StringComparison.OrdinalIgnoreCase))
            {
                var w = GetInt(el, "width");
                var h = GetInt(el, "height");
                if (w > 0 && h > 0) resolution = $"{w}x{h}";
                else if (vNone) resolution = string.Empty;
            }
            return new MediaFormat
            {
                Id = id,
                Ext = ext,
                AudioCodec = aNone ? string.Empty : acodec,
                Resolution = resolution,
                Bitrate = FormatKb(el, "abr") ?? FormatKb(el, "tbr") ?? string.Empty,
                Samplerate = FormatAsr(el),
                Language = language,
                IsAudioOnly = vNone && !aNone
            };
        }

        public static string ExtractLanguage(string? note)
        {
            if (string.IsNullOrWhiteSpace(note)) return string.Empty;
            var m = LanguageInNote.Match(note);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        static string SuggestFileName(string title, string ext)
        {
            var e = string.IsNullOrWhiteSpace(ext) ? "mp4" : ext.Trim().Trim('.');
            return SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "video" : title) + "." + e;
        }

        static string ExtractJsonObject(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end < start) return text;
            return text[start..(end + 1)];
        }

        static string? GetString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
        }

        static int GetInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
            return 0;
        }

        static string? FormatKb(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return null;
            return Math.Round(p.GetDouble()) + "k";
        }

        static string FormatAsr(JsonElement el)
        {
            if (!el.TryGetProperty("asr", out var p) || p.ValueKind != JsonValueKind.Number) return string.Empty;
            var v = p.GetInt32();
            if (v >= 1000) return (v / 1000) + "k";
            return v > 0 ? v + "Hz" : string.Empty;
        }

        static bool TryPathId(string path, string marker, out string id)
        {
            id = string.Empty;
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            id = path[(idx + marker.Length)..].Trim('/').Split('/')[0];
            return !string.IsNullOrWhiteSpace(id);
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            if (query.StartsWith('?')) query = query[1..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(part[..eq].Replace('+', ' '));
                var val = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                d[key] = val;
            }
            return d;
        }
    }

    internal sealed record PlaylistEntry(string Url, string Title);
}
