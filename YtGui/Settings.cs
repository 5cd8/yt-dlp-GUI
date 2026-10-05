using System;
using System.IO;
using System.Text.Json;

namespace YtGui
{
    public class Settings
    {
        public string YtDlpPath { get; set; } = string.Empty;
        public string FfmpegPath { get; set; } = string.Empty;
        public string TwitchChatToolPath { get; set; } = string.Empty;
        public string DefaultCookiePath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string EmojiCacheOutputDirectory { get; set; } = string.Empty;
        public bool UseNoPart { get; set; } = true;
        public int RetryCount { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;
        public string ChannelUrls { get; set; } = string.Empty;
        public int ChannelPrefetchCount { get; set; } = 5;

        internal static string GetDataDirectory()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YtGui");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string GetSettingsPath() => Path.Combine(GetDataDirectory(), "settings.json");

        public static Settings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path)) return new Settings();
                var json = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<Settings>(json);
                return s ?? new Settings();
            }
            catch
            {
                return new Settings();
            }
        }

        public void Save()
        {
            var path = GetSettingsPath();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, true);
        }
    }
}
