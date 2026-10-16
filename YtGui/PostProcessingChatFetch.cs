namespace YtGui
{
    internal static class PostProcessingChatFetch
    {
        // yt-dlp が、ファイルのダウンロードを終えた後の後処理に入ったときに、標準出力の行頭に出すタグ。
        // 一覧に無いタグは検知できず、チャット取得は動画取得の後に始まる（遅くなるだけで、取れなくはならない）。
        static readonly string[] PostProcessingLinePrefixes =
        {
            "[Merger]", "[ExtractAudio]", "[Metadata]", "[EmbedThumbnail]", "[MoveFiles]",
            "[VideoRemuxer]", "[VideoConvertor]", "[Fixup",
        };

        public static bool IsPostProcessingLine(string line)
        {
            foreach (var prefix in PostProcessingLinePrefixes)
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        // ライブ録画は対象にしない。YouTube は録画の直後にチャットリプレイがまだ無いことがあり、仕上げ（ffmpeg）が出力ファイル名を書き換えるため（docs/adr/0008参照）。
        public static bool ShouldFetchChatDuringPostProcessing(bool isLive, bool downloadChatReplay, SiteKind siteKind)
            => downloadChatReplay && !isLive && (siteKind is SiteKind.YouTube or SiteKind.Twitch);
    }
}
