using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AntigravityQuota
{
    internal static class Program
    {
        private const string MutexName = @"Global\AntigravityQuota_WebServer_2026";
        private const int BasePort = 8917;
        private const int PortAttempts = 12;

        private static int Main(string[] args)
        {
            try
            {
                // 开机自启开关：AntigravityQuota.exe --autostart on|off
                if (HasFlag(args, "--autostart"))
                {
                    bool enable = !args.Any(a => a.Equals("off", StringComparison.OrdinalIgnoreCase));
                    AutoStartHelper.SetAutoStart(enable);
                    Log(enable ? "已开启开机自启（静默启动，不弹浏览器）" : "已关闭开机自启");
                    return 0;
                }

                // 桌面快捷方式：AntigravityQuota.exe --install-shortcut | --uninstall-shortcut
                if (HasFlag(args, "--install-shortcut"))
                {
                    Log("创建桌面快捷方式 → " + ShortcutHelper.Install());
                    return 0;
                }
                if (HasFlag(args, "--uninstall-shortcut"))
                {
                    Log("删除桌面快捷方式 → " + ShortcutHelper.Uninstall());
                    return 0;
                }

                using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);

                if (!isFirstInstance)
                {
                    // 已经有一个服务在跑：直接把浏览器指过去，不再起第二个
                    var existing = RuntimeInfo.Read();
                    if (existing != null) OpenBrowser(existing.Port);
                    else Log("已有实例在运行，但读不到端口信息");
                    return 0;
                }

                var state = new AppState();
                state.Start();

                var server = new HttpServer(state);
                if (!server.Start(BasePort, PortAttempts))
                {
                    Log($"错误：{BasePort}~{BasePort + PortAttempts - 1} 端口全部被占用，启动失败");
                    return 1;
                }

                RuntimeInfo.Write(Environment.ProcessId, server.Port);

                // --no-browser：静默采集不打扰（开机自启用它）
                // --open：只打开浏览器
                bool noBrowser = HasFlag(args, "--no-browser");
                if (!noBrowser || HasFlag(args, "--open"))
                    OpenBrowser(server.Port);

                Log($"服务已启动: http://127.0.0.1:{server.Port}/  (pid={Environment.ProcessId})");

                // 常驻，直到进程被结束
                using var waitHandle = new ManualResetEvent(false);

                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    RuntimeInfo.Clear();
                    Log("服务已退出");
                };

                waitHandle.WaitOne();
                return 0;
            }
            catch (Exception ex)
            {
                Log("致命错误: " + ex);
                return 1;
            }
        }

        private static bool HasFlag(string[] args, string flag)
            => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        private static void OpenBrowser(int port)
        {
            try
            {
                Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log("打开浏览器失败: " + ex.Message);
            }
        }

        // ---------------- 日志 ----------------

        private static readonly object LogLock = new();

        internal static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AntigravityQuota");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "server.log");

                lock (LogLock)
                {
                    // 简单限长，避免日志无限增长
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Exists && info.Length > 1024 * 1024) File.Delete(file);
                    }
                    catch { }

                    File.AppendAllText(file,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 记录运行中的服务端口，用于「重复启动时只打开浏览器」以及 --open。
    /// 启动时若发现 pid 已不存在，视为陈旧记录自动清理。
    /// </summary>
    internal sealed class RuntimeInfo
    {
        public int Pid { get; set; }
        public int Port { get; set; }
        public string StartedAt { get; set; } = "";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AntigravityQuota", "runtime.json");

        public static void Write(int pid, int port)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath) ?? "";
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var info = new RuntimeInfo
                {
                    Pid = pid,
                    Port = port,
                    StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(info));
            }
            catch { }
        }

        /// <summary>读取仍在运行实例的端口；陈旧记录返回 null</summary>
        public static RuntimeInfo? Read()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;

                var info = JsonSerializer.Deserialize<RuntimeInfo>(File.ReadAllText(FilePath));
                if (info == null || info.Port <= 0) return null;

                // 校验进程是否还活着
                try
                {
                    using var proc = Process.GetProcessById(info.Pid);
                    if (proc.HasExited) throw new InvalidOperationException();
                }
                catch
                {
                    Clear();
                    return null;
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }
}
