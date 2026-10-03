using System.Diagnostics;
using System.Text.RegularExpressions;

namespace YtGui
{
    internal enum LivePartMergeOutcome { Merged, Failed, Canceled }

    internal enum LiveFinalizeAction { None, Rename, Merge, MergeSingle, RefuseUnexpectedCount }

    internal static class LivePartMerger
    {
        public static LiveFinalizeAction DecideFinalizeAction(bool hasSinglePart, bool outputExists, int splitPartCount)
        {
            if (hasSinglePart) return LiveFinalizeAction.Rename;
            // 録画が最後まで進むと、yt-dlp が自分で結合して <名前>.mp4 を作る。前の録画の途中ファイルが残っていても拾わない。
            if (outputExists) return LiveFinalizeAction.None;
            return splitPartCount switch
            {
                0 => LiveFinalizeAction.None,
                1 => LiveFinalizeAction.MergeSingle,
                2 => LiveFinalizeAction.Merge,
                _ => LiveFinalizeAction.RefuseUnexpectedCount,
            };
        }

        public static bool IsSplitPartFileName(string fileName, string baseName)
            // フォーマットIDは数字で始まる。`.f` で始まるだけの別の名前を拾わないよう、数字に限る。
            => Regex.IsMatch(fileName, "^" + Regex.Escape(baseName) + @"\.f[0-9][^.]*\.[^.]+(\.part)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static List<string> FindSplitParts(string outputPath)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return new List<string>();
            var baseName = Path.GetFileNameWithoutExtension(outputPath);
            return Directory.EnumerateFiles(dir)
                .Where(path => IsSplitPartFileName(Path.GetFileName(path), baseName))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> BuildMergeArgs(IReadOnlyList<string> partPaths, string outputPath)
        {
            var args = new List<string> { "-y" };
            foreach (var partPath in partPaths)
            {
                args.Add("-i");
                args.Add(partPath);
            }
            for (var i = 0; i < partPaths.Count; i++)
            {
                args.Add("-map");
                args.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            args.AddRange(new[] { "-c", "copy", "-shortest", outputPath });
            return args;
        }

        public static bool IsLeftoverOf(string fileName, string partFileName)
        {
            const string partSuffix = ".part";
            var downloadFileName = partFileName.EndsWith(partSuffix, StringComparison.OrdinalIgnoreCase)
                ? partFileName[..^partSuffix.Length]
                : partFileName;
            var ytdlFileName = downloadFileName + ".ytdl";
            return fileName.Equals(partFileName, StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(ytdlFileName, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(partFileName + "-Frag", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<LivePartMergeOutcome> MergeAsync(string ffmpegPath, IReadOnlyList<string> partPaths, string outputPath, CancellationToken token)
        {
            if (token.IsCancellationRequested) return LivePartMergeOutcome.Canceled;
            var merged = false;
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var arg in BuildMergeArgs(partPaths, outputPath)) psi.ArgumentList.Add(arg);

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, _) => { };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                using var reg = token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
                try
                {
                    await proc.WaitForExitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Kill の直後は ffmpeg がまだ出力ファイルを開いていて、待たずに消すと失敗しうる。
                    await proc.WaitForExitAsync();
                }
                merged = !token.IsCancellationRequested && proc.ExitCode == 0 && File.Exists(outputPath);
            }
            catch { }
            var canceled = !merged && token.IsCancellationRequested;
            if (!merged)
            {
                try { File.Delete(outputPath); } catch { }
            }
            if (canceled) return LivePartMergeOutcome.Canceled;
            return merged ? LivePartMergeOutcome.Merged : LivePartMergeOutcome.Failed;
        }

        public static void DeleteLeftovers(IReadOnlyList<string> partPaths)
        {
            foreach (var partPath in partPaths)
            {
                var dir = Path.GetDirectoryName(partPath);
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var partFileName = Path.GetFileName(partPath);
                foreach (var path in Directory.EnumerateFiles(dir).Where(path => IsLeftoverOf(Path.GetFileName(path), partFileName)).ToList())
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
    }
}
