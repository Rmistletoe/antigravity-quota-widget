using System;
using System.IO;
using System.Text.Json;

namespace AntigravityQuota
{
    public class AppConfig
    {
        // v2.0 起已无 GUI，原窗口位置/透明度/托盘等配置项一并移除。

        /// <summary>账本滚动保留月数</summary>
        public int UsageRetentionMonths { get; set; } = 3;

        /// <summary>用量扫描间隔（秒），最低 30 秒</summary>
        public int UsageScanIntervalSec { get; set; } = 60;

        /// <summary>是否已完成首次全量回填；置回 false 可在下次启动重建账本</summary>
        public bool UsageBackfillCompleted { get; set; } = false;
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
