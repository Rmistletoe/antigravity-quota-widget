using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfApplication = System.Windows.Application;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AntigravityQuota
{
    public partial class MainWindow : Window
    {
        private readonly QuotaService _service = new();
        private readonly DispatcherTimer _clockTimer = new();
        private QuotaStatus? _lastStatus;
        private DateTime _lastFetchTime = DateTime.MinValue;
        private Forms.NotifyIcon? _notifyIcon;
        private bool _isTopmost = true;
        private uint _wakeupMessageId = 0;

        // Win32 常量与结构体
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_SHOWWINDOW = 0x0018;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        public MainWindow()
        {
            InitializeComponent();

            // 初始屏幕定位 (右下角偏上)
            double screenW = SystemParameters.PrimaryScreenWidth;
            double screenH = SystemParameters.PrimaryScreenHeight;
            Left = screenW - 290;
            Top = screenH - 100;

            // 初始化系统右下角翡翠绿闪电托盘图标 (System Tray)
            InitNotifyIcon();

            // 1 秒轻量平滑倒计时定时器 (0 CPU 开销)
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => UpdateCountdownDisplay();
            _clockTimer.Start();

            // 启动后台异步轮询 (30秒一次)
            StartBackgroundPolling();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // 1. 设置 ToolWindow + Topmost 样式
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                // 2. 注册单例唤醒全局消息
                _wakeupMessageId = RegisterWindowMessage(App.WAKEUP_MSG);

                // 3. 安装底层 WndProc 钩子：拦截 Windows "显示桌面" (Win+D / 点击任务栏最右侧) 的强制隐藏指令
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 拦截外部重复启动时广播的单例唤醒信号
            if (_wakeupMessageId != 0 && msg == (int)_wakeupMessageId)
            {
                ApplyTopmost(true);
                Activate();
                handled = true;
                return IntPtr.Zero;
            }

            // 拦截 Windows "显示桌面" (点击任务栏最右端 / Win+D) 的隐藏窗口指令
            if (_isTopmost)
            {
                if (msg == WM_WINDOWPOSCHANGING)
                {
                    try
                    {
                        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                        if ((pos.flags & SWP_HIDEWINDOW) != 0)
                        {
                            pos.flags &= ~SWP_HIDEWINDOW;
                            pos.flags |= SWP_SHOWWINDOW;
                            pos.hwndInsertAfter = HWND_TOPMOST;
                            Marshal.StructureToPtr(pos, lParam, true);
                        }
                    }
                    catch { }
                }
                else if (msg == WM_SHOWWINDOW)
                {
                    if (wParam == IntPtr.Zero)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }

            return IntPtr.Zero;
        }

        public void ApplyTopmost(bool top)
        {
            _isTopmost = top;
            Topmost = top;
            MenuTopmostItem.IsChecked = top;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowPos(hwnd, top ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
            catch { }
        }

        private void InitNotifyIcon()
        {
            try
            {
                Icon appIcon = IconHelper.CreateQuotaIcon(32);

                _notifyIcon = new Forms.NotifyIcon
                {
                    Text = "Antigravity Quota Monitor",
                    Icon = appIcon,
                    Visible = true
                };

                // 托盘右键菜单
                var contextMenu = new Forms.ContextMenuStrip();
                contextMenu.Items.Add("🔄 立即刷新数据", null, (s, e) => TriggerManualRefresh());
                
                var topmostItem = new Forms.ToolStripMenuItem("📌 始终置顶") { Checked = _isTopmost };
                topmostItem.Click += (s, e) =>
                {
                    ApplyTopmost(!_isTopmost);
                    topmostItem.Checked = _isTopmost;
                };
                contextMenu.Items.Add(topmostItem);

                contextMenu.Items.Add(new Forms.ToolStripSeparator());
                contextMenu.Items.Add("❌ 退出程序", null, (s, e) => ExitApplication());

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) =>
                {
                    Activate();
                    ApplyTopmost(true);
                };
            }
            catch { }
        }

        private void StartBackgroundPolling()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var status = await _service.FetchQuotaAsync();
                        Dispatcher.Invoke(() =>
                        {
                            _lastStatus = status;
                            _lastFetchTime = DateTime.UtcNow;
                            RenderData();
                        });
                    }
                    catch { }

                    await Task.Delay(30000);
                }
            });
        }

        private void TriggerManualRefresh()
        {
            Task.Run(async () =>
            {
                var status = await _service.FetchQuotaAsync();
                Dispatcher.Invoke(() =>
                {
                    _lastStatus = status;
                    _lastFetchTime = DateTime.UtcNow;
                    RenderData();
                });
            });
        }

        // 任意位置按住左键直接拖拽，144Hz 满帧硬件加速
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep && FindParent<Border>(dep) == BtnClosePill)
                return;

            try
            {
                DragMove();
            }
            catch { }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T typed) return typed;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        // ----------------- 胶囊右上角关闭按钮 (✕) -----------------
        private void BtnClose_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ExitApplication();
        }

        private void BtnClose_MouseEnter(object sender, WpfMouseEventArgs e)
        {
            BtnClosePill.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
            ClosePillText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        }

        private void BtnClose_MouseLeave(object sender, WpfMouseEventArgs e)
        {
            BtnClosePill.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x0D, 0x00, 0x00, 0x00));
            ClosePillText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        // ----------------- 菜单操作 -----------------
        private void MenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            TriggerManualRefresh();
        }

        private void MenuTopmost_Click(object sender, RoutedEventArgs e)
        {
            ApplyTopmost(!_isTopmost);
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void ExitApplication()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            WpfApplication.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            base.OnClosed(e);
        }

        private SolidColorBrush GetStatusBrush(double pct)
        {
            if (pct >= 50) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // 绿色
            if (pct >= 20) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // 黄色
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // 红色
        }

        private void RenderData()
        {
            if (_lastStatus == null || !_lastStatus.Success)
            {
                PillPctText.Text = "连接中...";
                PillStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                return;
            }

            var hero = _lastStatus.PrimaryModel;
            if (hero == null) return;

            var brush = GetStatusBrush(hero.Percentage);

            // 1. 胶囊渲染
            string shortLabel = hero.DisplayLabel.Replace("Gemini ", "").Replace(" (High)", "");
            PillPctText.Text = $"{shortLabel}: {hero.Percentage:F1}%";
            PillStatusDot.Fill = brush;
            PillStatusGlow.Color = brush.Color;

            // 2. 鼠标悬停 ToolTip 渲染
            TipUserText.Text = $"👤 {_lastStatus.UserName} · {_lastStatus.PlanName} 套餐";

            TipModelsContainer.Children.Clear();
            foreach (var m in _lastStatus.Models.Take(5))
            {
                var mBrush = GetStatusBrush(m.Percentage);

                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

                var lblName = new TextBlock
                {
                    Text = m.DisplayLabel,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x41, 0x55)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(lblName, 0);

                var pb = new WpfProgressBar
                {
                    Height = 4,
                    Value = m.Percentage,
                    Maximum = 100,
                    Foreground = mBrush,
                    Style = (Style)FindResource("LightProgressBarStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                Grid.SetColumn(pb, 1);

                var lblVal = new TextBlock
                {
                    Text = $"{m.Percentage:F1}%",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = mBrush,
                    HorizontalAlignment = WpfHorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lblVal, 2);

                row.Children.Add(lblName);
                row.Children.Add(pb);
                row.Children.Add(lblVal);

                TipModelsContainer.Children.Add(row);
            }

            // 更新托盘提示文字
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = $"Antigravity: {shortLabel} {hero.Percentage:F1}%";
            }

            UpdateCountdownDisplay();
        }

        private void UpdateCountdownDisplay()
        {
            if (_lastStatus == null || !_lastStatus.Success) return;
            var hero = _lastStatus.PrimaryModel;
            if (hero == null) return;

            int elapsed = (int)(DateTime.UtcNow - _lastFetchTime).TotalSeconds;
            int remSecs = Math.Max(0, hero.ResetSeconds - elapsed);

            string formatted = FormatTime(remSecs);
            PillCountdownText.Text = formatted;
            TipCountdownText.Text = $"{formatted} 后";
        }

        private string FormatTime(int secs)
        {
            if (secs <= 0) return "已重置";
            int h = secs / 3600;
            int m = (secs % 3600) / 60;
            int s = secs % 60;
            return h > 0 ? $"{h:D2}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
        }
    }
}
