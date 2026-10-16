using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace YtGui
{
    // 外部ツール（ffmpeg・TwitchDownloaderCLI）の版の読み取りと比較。UIに依存しない（docs/adr/0009参照）。
    internal static class ToolVersions
    {
        // 出力のどこかの行が「<プログラム名> version n<X>.<Y>[.<Z>]」で始まること。標準エラーの行が先に混ざっても読めるよう、どの行でもよい。
        // 「n」が無い形（Gyan のビルドなど）や、master の「N-126313-g…」は読めない（Issue #44 の要件8。読めない版は「更新が必要」）。
        public static bool TryParseFfmpegFamilyVersion(string? output, string programName, [NotNullWhen(true)] out Version? version)
        {
            version = null;
            if (string.IsNullOrEmpty(output)) return false;
            var match = Regex.Match(output, "^" + Regex.Escape(programName) + @" version n(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.Multiline);
            return match.Success && TryBuildVersion(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Success ? match.Groups[3].Value : "0", out version);
        }

        // 出力のどこにあってもよい。TwitchDownloaderCLI --version は、バージョンを標準エラーにだけ出し、終了コードは1を返す。
        public static bool TryParseTwitchToolVersion(string? output, [NotNullWhen(true)] out Version? version)
        {
            version = null;
            if (string.IsNullOrEmpty(output)) return false;
            var match = Regex.Match(output, @"TwitchDownloaderCLI\s+(\d+)\.(\d+)\.(\d+)");
            return match.Success && TryBuildVersion(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, out version);
        }

        public static bool TryParseReleaseTag(string? tag, [NotNullWhen(true)] out Version? version)
        {
            version = null;
            if (string.IsNullOrEmpty(tag)) return false;
            var match = Regex.Match(tag, @"^v?(\d+)\.(\d+)\.(\d+)$");
            return match.Success && TryBuildVersion(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, out version);
        }

        // 読めない版（installed が null）は更新が必要。今の版のほうが新しい（同じ版を含む）なら更新しない。
        public static bool IsUpdateNeeded(Version? installed, Version latest)
            => installed == null || latest > installed;

        // Version は 3つ目が無いと Build が -1 になり、9.0 < 9.0.0 になるので、3つとも渡して作る。
        internal static bool TryBuildVersion(string major, string minor, string patch, [NotNullWhen(true)] out Version? version)
        {
            version = null;
            if (!int.TryParse(major, out var ma) || !int.TryParse(minor, out var mi) || !int.TryParse(patch, out var pa)) return false;
            version = new Version(ma, mi, pa);
            return true;
        }
    }
}
