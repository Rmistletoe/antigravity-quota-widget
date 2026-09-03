using System;
using System.IO;
using System.Text.Json;

namespace AntigravityQuota
{
    public class AppConfig
    {
        public double WindowLeft { get; set; } = -1;
        public double WindowTop { get; set; } = -1;
        public string MainDisplayMode { get; set; } = "Gemini"; // "Gemini" | "Claude"
        public double Opacity { get; set; } = 1.0;
        public bool AutoStart { get; set; } = false;
        public bool RecoveryNotifyEnabled { get; set; } = true;
    }

    public static class ConfigManager
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AntigravityQuota"
        );
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static AppConfig Current { get; private set; } = new();

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        Current = cfg;
                        return Current;
                    }
                }
            }
            catch { }

            Current = new AppConfig();
            Save();
            return Current;
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }
                string json = JsonSerializer.Serialize(Current, JsonOpts);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
