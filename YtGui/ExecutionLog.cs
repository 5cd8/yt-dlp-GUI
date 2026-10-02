namespace YtGui
{
    internal static class ExecutionLogFilter
    {
        public static bool IsFfmpegNoiseLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("frame=", StringComparison.Ordinal)) return true;
            if (trimmed.StartsWith("size=", StringComparison.Ordinal)) return true;
            return trimmed.Contains("] Opening '", StringComparison.Ordinal)
                && trimmed.EndsWith("' for reading", StringComparison.Ordinal);
        }
    }

    internal sealed class ExecutionLogWriter
    {
        const int MaxLines = 2000;
        const int LinesAfterTrim = 1500;

        readonly TextBox textBox;
        // textBox のテキストは、このクラスだけが書き換える前提で、改行の数を数えておく。
        // 毎回 Lines で数え直すと、それ自体がテキスト全体をなめる処理になる。
        int lineCount;

        public ExecutionLogWriter(TextBox textBox) => this.textBox = textBox;

        public void Append(string text)
        {
            textBox.AppendText(text);
            lineCount += text.Count(c => c == '\n');
            if (lineCount <= MaxLines) return;
            TrimToRecentLines();
        }

        void TrimToRecentLines()
        {
            var lines = textBox.Lines;
            // テキストは改行で終わるので、Lines の最後は空文字列になる。最新の LinesAfterTrim 行と、その空文字列を残す。
            textBox.Lines = lines[^(LinesAfterTrim + 1)..];
            lineCount = LinesAfterTrim;
            textBox.Select(textBox.TextLength, 0);
            textBox.ScrollToCaret();
        }
    }
}
