using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtGui
{
    internal sealed record ToolRelease(Version Version, string AssetName, string DownloadUrl, long Size, string Sha256Hex);

    // GitHub API の JSON から、更新するアセットを選ぶ。UIに依存しない（docs/adr/0009参照）。
    internal static class ToolReleaseSources
    {
        // release ブランチ追従の static ビルド（gpl）。コミット数とハッシュは、リリースブランチがタグちょうどの日は付かない。
        // master（N-…）・shared・lgpl は一致しない。
        static readonly Regex FfmpegAssetPattern = new(
            @"^ffmpeg-n(\d+)\.(\d+)(?:\.(\d+))?(?:-\d+-g[0-9a-f]+)?-win64-gpl-(\d+)\.(\d+)\.zip$", RegexOptions.CultureInvariant);

        const int MaxAutobuildReleases = 10;

        // releases は GET /repos/BtbN/FFmpeg-Builds/releases の配列。API が返す順には頼らず、tag_name の降順で見る。
        // ブランチ（X.Y）は調べる範囲の全アセットから最大のものを1つ決め（最新のビルドが失敗した日に古いブランチへ切り替わらないため）、
        // そのブランチのアセットを持つ新しいリリースから、digest が読める最初のものを返す。
        public static ToolRelease? SelectFfmpegRelease(JsonElement releases)
        {
            if (releases.ValueKind != JsonValueKind.Array) return null;

            var autobuilds = new List<(string Tag, JsonElement Assets)>();
            foreach (var release in releases.EnumerateArray())
            {
                var tag = GetString(release, "tag_name");
                if (tag == null || !tag.StartsWith("autobuild-", StringComparison.Ordinal)) continue;
                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                autobuilds.Add((tag, assets));
            }
            autobuilds.Sort((a, b) => string.CompareOrdinal(b.Tag, a.Tag));
            if (autobuilds.Count > MaxAutobuildReleases) autobuilds.RemoveRange(MaxAutobuildReleases, autobuilds.Count - MaxAutobuildReleases);

            Version? maxBranch = null;
            foreach (var (_, assets) in autobuilds)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!TryMatchFfmpegAsset(GetString(asset, "name"), out _, out var branch)) continue;
                    if (maxBranch == null || branch > maxBranch) maxBranch = branch;
                }
            }
            if (maxBranch == null) return null;

            foreach (var (_, assets) in autobuilds)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    if (!TryMatchFfmpegAsset(name, out var version, out var branch) || branch != maxBranch) continue;
                    var sha = ParseSha256Digest(GetString(asset, "digest"));
                    var url = GetString(asset, "browser_download_url");
                    if (sha == null || url == null || name == null) continue;
                    return new ToolRelease(version, name, url, GetLong(asset, "size"), sha);
                }
            }
            return null;
        }

        // release は GET /repos/lay295/TwitchDownloader/releases/latest の1件。
        public static ToolRelease? SelectTwitchToolRelease(JsonElement release)
        {
            if (release.ValueKind != JsonValueKind.Object) return null;
            var tag = GetString(release, "tag_name");
            if (!ToolVersions.TryParseReleaseTag(tag, out var version)) return null;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            var expectedName = "TwitchDownloaderCLI-" + tag + "-Windows-x64.zip";
            foreach (var asset in assets.EnumerateArray())
            {
                if (GetString(asset, "name") != expectedName) continue;
                var sha = ParseSha256Digest(GetString(asset, "digest"));
                var url = GetString(asset, "browser_download_url");
                if (sha == null || url == null) return null;
                return new ToolRelease(version, expectedName, url, GetLong(asset, "size"), sha);
            }
            return null;
        }

        // "sha256:<64桁の16進>" → 小文字の16進。それ以外は null。
        public static string? ParseSha256Digest(string? digest)
        {
            const string prefix = "sha256:";
            if (digest == null || !digest.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var hex = digest[prefix.Length..];
            if (hex.Length != 64) return null;
            foreach (var c in hex)
                if (!Uri.IsHexDigit(c)) return null;
            return hex.ToLowerInvariant();
        }

        static bool TryMatchFfmpegAsset(string? name, out Version version, out Version branch)
        {
            version = new Version(0, 0, 0);
            branch = new Version(0, 0);
            if (name == null) return false;
            var match = FfmpegAssetPattern.Match(name);
            if (!match.Success) return false;
            if (!ToolVersions.TryBuildVersion(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Success ? match.Groups[3].Value : "0", out var v)) return false;
            if (!int.TryParse(match.Groups[4].Value, out var branchMajor) || !int.TryParse(match.Groups[5].Value, out var branchMinor)) return false;
            version = v;
            branch = new Version(branchMajor, branchMinor);
            return true;
        }

        static string? GetString(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static long GetLong(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                ? number
                : 0;
    }
}
