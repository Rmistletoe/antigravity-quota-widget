using System;
using System.IO;

namespace AntigravityQuota
{
    /// <summary>
    /// 桌面快捷方式的创建/删除。
    /// 走 WScript.Shell 的 IDispatch 晚绑定（dynamic），不需要手写 IShellLink 的
    /// vtable 声明 —— 那玩意儿接口方法顺序错一个就会崩，风险不值得。
    /// </summary>
    internal static class ShortcutHelper
    {
        private const string LinkName = "Antigravity 用量面板.lnk";

        private static string DesktopDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        private static string LinkPath => Path.Combine(DesktopDir, LinkName);

        private static dynamic? CreateShell()
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            return t == null ? null : Activator.CreateInstance(t);
        }

        /// <summary>在桌面创建（或覆盖）快捷方式，返回可读的结果描述</summary>
        public static string Install()
        {
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return "失败：拿不到当前 exe 路径";

            dynamic? shell = CreateShell();
            if (shell == null) return "失败：系统未注册 WScript.Shell";

            try
            {
                dynamic sc = shell.CreateShortcut(LinkPath);
                sc.TargetPath = exe;
                sc.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
                sc.IconLocation = exe + ",0";
                sc.Description = "Antigravity 用量面板 - 双击打开面板（已在运行则只打开页面）";
                sc.WindowStyle = 1;
                sc.Save();
            }
            catch (Exception ex)
            {
                return "失败：" + ex.Message;
            }

            // 读回校验，确认真的写进去了
            try
            {
                dynamic chk = shell.CreateShortcut(LinkPath);
                string target = (string)chk.TargetPath;
                return $"成功：{LinkPath}\n         目标={target}\n         起始位置={(string)chk.WorkingDirectory}"
                     + $"\n         图标={(string)chk.IconLocation}";
            }
            catch
            {
                return $"已创建（读回校验略过）：{LinkPath}";
            }
        }

        public static string Uninstall()
        {
            try
            {
                if (File.Exists(LinkPath)) { File.Delete(LinkPath); return "已删除：" + LinkPath; }
                return "桌面上没有该快捷方式";
            }
            catch (Exception ex)
            {
                return "失败：" + ex.Message;
            }
        }
    }
}
