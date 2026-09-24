using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AntigravityQuota
{
    /// <summary>
    /// 系统托盘图标。
    /// 继承 ApplicationContext 是为了让 Application.Run() 有个消息泵 ——
    /// NotifyIcon 依赖 Windows 消息循环，主线程如果只是 WaitOne 阻塞着，托盘点了不会有反应。
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly AppState _state;
        private readonly int _port;
        private readonly NotifyIcon _tray;
        // 必须用 WinForms 的 Timer：它跑在 UI 线程上，能安全改托盘文字；
        // System.Threading.Timer 会在后台线程回调，NotifyIcon 会抛跨线程异常。
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ToolStripMenuItem _autoStartItem;

        public TrayApp(AppState state, int port)
        {
            _state = state;
            _port = port;

            _autoStartItem = new ToolStripMenuItem("随 Windows 开机启动")
            {
                CheckOnClick = true,
                Checked = AutoStartHelper.IsAutoStartEnabled()
            };
            _autoStartItem.Click += (s, e) =>
            {
                AutoStartHelper.SetAutoStart(_autoStartItem.Checked);
                Program.Log(_autoStartItem.Checked ? "已开启开机自启" : "已关闭开机自启");
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(NewItem("打开面板", OpenPanel, bold: true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(NewItem("立即刷新数据", () => _state.RequestRefresh()));
            menu.Items.Add(NewItem("复制面板地址", CopyUrl));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(NewItem("打开数据目录", OpenDataDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(NewItem("退出", ExitApp));

            _tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Antigravity 用量面板",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => OpenPanel();

            // 托盘悬停文字最多 63 字符，5 秒刷一次就够
            _timer = new System.Windows.Forms.Timer { Interval = 5000 };
            _timer.Tick += (s, e) => RefreshTooltip();
            _timer.Start();
            RefreshTooltip();
        }

        private static ToolStripMenuItem NewItem(string text, Action onClick, bool bold = false)
        {
            var item = new ToolStripMenuItem(text);
            if (bold) item.Font = new Font(item.Font, FontStyle.Bold);
            item.Click += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { Program.Log($"托盘菜单「{text}」出错: {ex.Message}"); }
            };
            return item;
        }

        // ---------------- 面板 ----------------

        private string Url => $"http://127.0.0.1:{_port}/";

        /// <summary>在默认浏览器的标签页里打开面板</summary>
        private void OpenPanel() => PanelOpener.OpenInBrowser(Url);

        private void CopyUrl()
        {
            try
            {
                Clipboard.SetText(Url);
                _tray.ShowBalloonTip(2000, "已复制", Url, ToolTipIcon.Info);
            }
            catch (Exception ex) { Program.Log("复制失败: " + ex.Message); }
        }

        private static void OpenDataDir()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AntigravityQuota");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { Program.Log("打开数据目录失败: " + ex.Message); }
        }

        private void ExitApp()
        {
            Program.Log("用户从托盘退出");
            Program.CleanupRuntime();
            ExitThread();
        }

        // ---------------- 托盘状态 ----------------

        private void RefreshTooltip()
        {
            try
            {
                string text = _state.BuildTrayTooltip();
                // NotifyIcon.Text 上限 63 字符，超了会抛异常，必须截断
                if (text.Length > 62) text = text.Substring(0, 61) + "…";
                _tray.Text = text;
            }
            catch { }
        }

        private static Icon LoadIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var ico = Icon.ExtractAssociatedIcon(exe);
                    if (ico != null) return ico;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        protected override void ExitThreadCore()
        {
            try
            {
                _timer.Stop();
                _timer.Dispose();
                _tray.Visible = false;   // 不设 false 会在托盘留个幽灵图标，要鼠标划过才消失
                _tray.Dispose();
            }
            catch { }
            base.ExitThreadCore();
        }
    }

    /// <summary>负责把面板送到用户眼前的默认浏览器里。</summary>
    internal static class PanelOpener
    {
        public static void OpenInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Program.Log("打开浏览器失败: " + ex.Message);
            }
        }
    }
}
