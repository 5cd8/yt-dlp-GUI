using System.Diagnostics;

namespace YtGui
{
    internal enum ThumbnailEmbedOutcome { NoThumbnail, Embedded, Failed, Canceled }

    internal static class ThumbnailEmbedder
    {
        public static async Task<ThumbnailEmbedOutcome> EmbedAsync(string ffmpegPath, string videoPath, CancellationToken token)
        {
            var dir = Path.GetDirectoryName(videoPath);
            var baseName = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrWhiteSpace(dir)) return ThumbnailEmbedOutcome.NoThumbnail;
            string[] thumbExts = { ".webp", ".jpg", ".jpeg", ".png" };
            var thumbPath = thumbExts
                .Select(ext => Path.Combine(dir, baseName + ext))
                .FirstOrDefault(File.Exists);
            if (thumbPath == null) return ThumbnailEmbedOutcome.NoThumbnail;
            if (token.IsCancellationRequested) return ThumbnailEmbedOutcome.Canceled;

            var tempPath = Path.Combine(dir, baseName + ".thumbtmp.mp4");
            var embedded = false;
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(thumbPath);
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-c:v:1"); psi.ArgumentList.Add("mjpeg");
                psi.ArgumentList.Add("-disposition:v:1"); psi.ArgumentList.Add("attached_pic");
                psi.ArgumentList.Add(tempPath);

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
                    // Kill の直後は ffmpeg がまだ一時ファイルを開いていて、待たずに消すと失敗しうる。
                    await proc.WaitForExitAsync();
                }
                // ffmpeg が成功した直後に中止された場合も、ユーザーが中止を押した以上は中止を優先する。
                embedded = !token.IsCancellationRequested && proc.ExitCode == 0 && File.Exists(tempPath);
            }
            catch { }
            // 例外で外側の catch に落ちた場合も中止なら画像を残すため、try の外でトークンを見る。
            var canceled = token.IsCancellationRequested;

            try
            {
                if (embedded)
                {
                    File.Replace(tempPath, videoPath, null);
                }
                else
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            catch { }
            // 中止したときは画像を消さない。埋め込みを諦めたうえに画像まで消すと、サムネイルが手元に何も残らない。
            if (!canceled)
            {
                try { File.Delete(thumbPath); } catch { }
            }

            if (canceled) return ThumbnailEmbedOutcome.Canceled;
            return embedded ? ThumbnailEmbedOutcome.Embedded : ThumbnailEmbedOutcome.Failed;
        }
    }
}
