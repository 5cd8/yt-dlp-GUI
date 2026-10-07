namespace YtGui
{
    internal enum EmojiCacheOutputState
    {
        NotConfigured,
        Missing,
        Exists,
    }

    internal static class EmojiCacheOutputPath
    {
        // 自動作成はしない。出力先は vlc-chat が読むフォルダで、誤ったパスに作ると別の場所へ投入し続けるため。
        // 親フォルダの有無は区別しない。ファイルを指すパスや使えない文字を含むパスも、Directory.Exists が偽になるので「存在しない」。
        public static EmojiCacheOutputState Classify(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return EmojiCacheOutputState.NotConfigured;
            return Directory.Exists(directory) ? EmojiCacheOutputState.Exists : EmojiCacheOutputState.Missing;
        }

        public static string BuildMissingMessage(string directory) => "出力先が存在しません: " + directory;
    }
}
