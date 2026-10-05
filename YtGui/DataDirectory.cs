namespace YtGui
{
    // データフォルダ（設定・エラーログ・処理済みアーカイブの記録を置く）の決定と、旧フォルダからの移行。
    internal static class DataDirectory
    {
        public const string SettingsFileName = "settings.json";
        public const string ErrorLogFileName = "last_error.log";
        public const string ProcessedArchivesFileName = "prefetched_archives.txt";

        static readonly string[] MigratedFileNames = { SettingsFileName, ErrorLogFileName, ProcessedArchivesFileName };

        // primary に書き込めるなら primary（旧フォルダにあって primary に無いファイルは移行する）、書き込めなければ legacy を返す。
        public static string ResolveDirectory(string primary, string legacy)
        {
            if (CanWrite(primary))
            {
                MigrateFiles(primary, legacy);
                return primary;
            }
            Directory.CreateDirectory(legacy);
            return legacy;
        }

        // Directory.CreateDirectory が成功するだけでは、読み取り専用や ACL で書き込みを拒否されたフォルダを見抜けない
        // （後の設定の保存で初めて失敗する）。書き込めないと分かってから従来の場所へ戻すため、実際に書いてみる。
        static bool CanWrite(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, Array.Empty<byte>());
                try { File.Delete(probe); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // 元のファイルは消さない。移行の結果はログに出さない（GetDataDirectory はフォームを作る前の Settings.Load から呼ばれる）。
        static void MigrateFiles(string primary, string legacy)
        {
            foreach (var name in MigratedFileNames)
            {
                var destination = Path.Combine(primary, name);
                var source = Path.Combine(legacy, name);
                var temporary = destination + ".migrating";
                try
                {
                    if (File.Exists(destination) || !File.Exists(source)) continue;
                    // 本来の名前へ直接 File.Copy しない。途中で失敗した壊れたファイルが、次回以降「移行済み」として固定されるため。
                    File.Copy(source, temporary, overwrite: true);
                    File.Move(temporary, destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    try { File.Delete(temporary); }
                    catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }
}
