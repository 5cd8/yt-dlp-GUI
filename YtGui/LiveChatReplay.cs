namespace YtGui
{
    internal static class LiveChatReplay
    {
        public static bool IsFetchedAfterRecording(bool isLive, bool downloadChatReplay, SiteKind siteKind)
            => isLive && downloadChatReplay && siteKind == SiteKind.YouTube;

        public static string BuildStatusText(string status, bool isChatReplayMissing)
            => isChatReplayMissing && (status is "ダウンロード完了" or "キャンセル")
                ? status + "（チャット未取得）"
                : status;

        public static bool CanEnqueueChatReplayFetch(string status, bool isChatReplayMissing, bool shouldFetchChatReplayOnly)
            => isChatReplayMissing && !shouldFetchChatReplayOnly && (status is "ダウンロード完了" or "キャンセル");

        public static List<string> BuildDownloadArgs(string url, string chatJsonPath)
        {
            const string suffix = ".live_chat.json";
            var basePath = chatJsonPath[..^suffix.Length];
            // -o は出力テンプレートとして解釈されるので、パスに含まれる % をそのまま渡すと置き換えの指示と読まれる。
            var template = basePath.Replace("%", "%%") + ".%(ext)s";
            return new List<string>
            {
                "--no-playlist", "--encoding", "utf-8",
                "--js-runtimes", "deno", "--remote-components", "ejs:github",
                "--skip-download", "--write-subs", "--sub-langs", "live_chat",
                // 配信中は、チャットリプレイではなく配信中のチャットを配信の終わりまで取り続けてしまう。
                "--match-filter", "live_status!=is_live & live_status!=is_upcoming",
                "-o", template, url,
            };
        }
    }
}
