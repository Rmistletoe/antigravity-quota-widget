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
        private object? _cachedPillToolTip;
        private bool _isDetailsOpen = false;
        private bool _isExpandedUpward = false;
        private DispatcherTimer? _toastTimer;

        private AppConfig _config = new();
        private double _lastNotifiedGeminiPct = 100.0;

        // Win32 常量与结构体
        private const int GWL_EXSTYLE = -20;
        private const int GWL_HWNDPARENT = -8;
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
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        public MainWindow()
        {
            InitializeComponent();
            _cachedPillToolTip = PillBorder.ToolTip;

            // 载入本地配置
            _config = ConfigManager.Load();

            // 屏幕定位 (优先恢复记忆位置)
            double screenW = SystemParameters.VirtualScreenWidth;
            double screenH = SystemParameters.VirtualScreenHeight;
            if (_config.WindowLeft >= 0 && _config.WindowTop >= 0 &&
                _config.WindowLeft < screenW - 40 && _config.WindowTop < screenH - 40)
            {
                Left = _config.WindowLeft;
                Top = _config.WindowTop;
            }
            else
            {
                Left = SystemParameters.PrimaryScreenWidth - 360;
                Top = SystemParameters.PrimaryScreenHeight - 100;
            }
            UpdateChevron();

            // 应用透明度与菜单状态
            ApplyOpacity(_config.Opacity);
            SyncMenuStates();

            // 初始化系统右下角翡翠绿闪电托盘图标 (System Tray)
            InitNotifyIcon();

            // 1 秒轻量平滑倒计时定时器 (0 CPU 开销)
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) =>
            {
                UpdateCountdownDisplay();
                if (_isTopmost) EnsureTopmostLevel();
            };
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

                // 2. 将宿主所有者绑定到任务栏 Shell_TrayWnd（彻底解决放在任务栏区域被任务栏覆盖隐藏的问题）
                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    try
                    {
                        SetWindowLongPtr(hwnd, GWL_HWNDPARENT, taskbarHwnd);
                    }
                    catch { }
                }

                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                // 3. 注册单例唤醒全局消息
                _wakeupMessageId = RegisterWindowMessage(App.WAKEUP_MSG);

                // 4. 安装底层 WndProc 钩子：拦截 Windows "显示桌面" (Win+D / 点击任务栏最右侧) 的强制隐藏指令
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
        }

        private void EnsureTopmostLevel()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowPos(hwnd, _isTopmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
            catch { }
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

        // 悬浮胶囊按下：智能区分拖拽与点击展开/收起
        private void PillBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep && FindParent<Border>(dep) == BtnClosePill)
                return;

            double startLeft = Left;
            double startTop = Top;
            try
            {
                DragMove();
            }
            catch { }
            if (_isTopmost) EnsureTopmostLevel();

            // 若位移小于 4 像素，判定为单击切换详情面板展开/收起
            double deltaX = Math.Abs(Left - startLeft);
            double deltaY = Math.Abs(Top - startTop);
            if (deltaX < 4 && deltaY < 4)
            {
                ToggleDetailsPanel();
            }
            else
            {
                UpdateChevron();

                // 保存拖拽后的屏幕位置
                _config.WindowLeft = Left;
                _config.WindowTop = Top;
                ConfigManager.Save();
            }
        }

        private void DetailsHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DragMove();
                if (_isTopmost) EnsureTopmostLevel();
            }
            catch { }
            UpdateChevron();

            _config.WindowLeft = Left;
            _config.WindowTop = Top;
            ConfigManager.Save();
        }

        private void DetailsCollapse_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleDetailsPanel();
        }

        private void DetailsRefresh_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            TriggerManualRefresh();
        }

        private void MenuToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            ToggleDetailsPanel();
        }

        public void ToggleDetailsPanel()
        {
            _isDetailsOpen = !_isDetailsOpen;
            if (_isDetailsOpen)
            {
                // 检测当前悬浮球所处的屏幕位置（上半屏还是下半屏）
                double screenWorkH = SystemParameters.WorkArea.Height;
                double pillScreenCenterY = Top + (ActualHeight / 2);
                _isExpandedUpward = pillScreenCenterY > (screenWorkH / 2);

                double currentBottom = Top + ActualHeight;
                double currentTop = Top;

                // 动态调整主容器中胶囊与详情板的相对顺序
                MainContainer.Children.Clear();
                if (_isExpandedUpward)
                {
                    // 在屏幕下方：详情板放在悬浮胶囊上方，胶囊保持在最底下不动
                    DetailsCardBorder.Margin = new Thickness(0, 0, 0, 8);
                    MainContainer.Children.Add(DetailsCardBorder);
                    MainContainer.Children.Add(PillBorder);
                }
                else
                {
                    // 在屏幕上方：详情板放在悬浮胶囊下方，胶囊保持在最顶上不动
                    DetailsCardBorder.Margin = new Thickness(0, 8, 0, 0);
                    MainContainer.Children.Add(PillBorder);
                    MainContainer.Children.Add(DetailsCardBorder);
                }

                DetailsCardBorder.Visibility = Visibility.Visible;
                PillBorder.ToolTip = null; // 展开面板时临时禁用悬停气泡，避免干扰

                UpdateLayout();

                if (_isExpandedUpward)
                {
                    // 向上展开：保持悬浮胶囊底边屏幕绝对位置不变
                    Top = currentBottom - ActualHeight;
                    if (Top < SystemParameters.WorkArea.Top)
                    {
                        Top = SystemParameters.WorkArea.Top;
                    }
                }
                else
                {
                    // 向下展开：保持悬浮胶囊顶边屏幕绝对位置不变
                    Top = currentTop;
                    double screenBottom = SystemParameters.WorkArea.Bottom;
                    if (Top + ActualHeight > screenBottom)
                    {
                        Top = Math.Max(SystemParameters.WorkArea.Top, screenBottom - ActualHeight - 8);
                    }
                }

                UpdateChevron();
            }
            else
            {
                double currentBottom = Top + ActualHeight;
                double currentTop = Top;

                DetailsCardBorder.Visibility = Visibility.Collapsed;
                UpdateLayout();

                if (_isExpandedUpward)
                {
                    // 向上展开收起时：保持悬浮胶囊底边屏幕位置不变
                    Top = currentBottom - ActualHeight;
                }
                else
                {
                    // 向下展开收起时：保持悬浮胶囊顶边屏幕位置不变
                    Top = currentTop;
                }

                PillBorder.ToolTip = _cachedPillToolTip; // 收起面板后恢复悬停气泡
                UpdateChevron();
            }
            EnsureTopmostLevel();
        }

        private void UpdateChevron()
        {
            double screenWorkH = SystemParameters.WorkArea.Height;
            double pillScreenCenterY = Top + (ActualHeight / 2);
            bool isBottomHalf = pillScreenCenterY > (screenWorkH / 2);

            if (_isDetailsOpen)
            {
                PillChevron.Text = _isExpandedUpward ? "▾" : "▴";
            }
            else
            {
                PillChevron.Text = isBottomHalf ? "▴" : "▾";
            }
        }

        private void ShowToast(string msg)
        {
            DetailsToastText.Text = msg;
            ToastBorder.Visibility = Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _toastTimer.Tick += (s, e) =>
                {
                    ToastBorder.Visibility = Visibility.Collapsed;
                    _toastTimer.Stop();
                };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
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

        private void MenuDisplayMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string mode)
            {
                _config.MainDisplayMode = mode;
                ConfigManager.Save();
                SyncMenuStates();
                RenderData();
            }
        }

        private void MenuOpacity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double op))
            {
                ApplyOpacity(op);
                ConfigManager.Save();
                SyncMenuStates();
            }
        }

        private void MenuRecoveryNotify_Click(object sender, RoutedEventArgs e)
        {
            _config.RecoveryNotifyEnabled = !_config.RecoveryNotifyEnabled;
            ConfigManager.Save();
            SyncMenuStates();
        }

        private void MenuAutoStart_Click(object sender, RoutedEventArgs e)
        {
            bool current = AutoStartHelper.IsAutoStartEnabled();
            AutoStartHelper.SetAutoStart(!current);
            _config.AutoStart = !current;
            ConfigManager.Save();
            SyncMenuStates();
        }

        private void SyncMenuStates()
        {
            MenuDisplayGemini.IsChecked = _config.MainDisplayMode == "Gemini";
            MenuDisplayClaude.IsChecked = _config.MainDisplayMode == "Claude";

            MenuOpacity100.IsChecked = Math.Abs(_config.Opacity - 1.0) < 0.05;
            MenuOpacity85.IsChecked = Math.Abs(_config.Opacity - 0.85) < 0.05;
            MenuOpacity70.IsChecked = Math.Abs(_config.Opacity - 0.70) < 0.05;
            MenuOpacity50.IsChecked = Math.Abs(_config.Opacity - 0.50) < 0.05;

            MenuRecoveryNotifyItem.IsChecked = _config.RecoveryNotifyEnabled;
            MenuAutoStartItem.IsChecked = AutoStartHelper.IsAutoStartEnabled();
        }

        private void ApplyOpacity(double opacity)
        {
            _config.Opacity = opacity;
            PillBorder.Opacity = opacity;
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void ExitApplication()
        {
            _config.WindowLeft = Left;
            _config.WindowTop = Top;
            ConfigManager.Save();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            WpfApplication.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _config.WindowLeft = Left;
            _config.WindowTop = Top;
            ConfigManager.Save();

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
                Pill5hText.Text = "连接中...";
                PillWeeklyText.Text = "周: --%";
                PillStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                return;
            }

            var geminiGroup = _lastStatus.GeminiGroup;
            var claudeGroup = _lastStatus.ClaudeGptGroup;

            // 根据用户设置的常驻核心模型，确定活跃的 5小时 额度桶与周额度桶
            bool isClaude = _config.MainDisplayMode == "Claude";
            var activeGroup = isClaude ? (claudeGroup ?? geminiGroup) : geminiGroup;
            var active5h = activeGroup?.FiveHourBucket;
            var activeWeekly = activeGroup?.WeeklyBucket ?? geminiGroup?.WeeklyBucket;

            if (isClaude)
            {
                Pill5hText.Text = active5h != null ? $"Sonnet 4.6: {active5h.Percentage:F1}%" : "Sonnet 4.6: --%";
            }
            else
            {
                Pill5hText.Text = active5h != null ? $"3.8 Flash: {active5h.Percentage:F1}%" : "3.8 Flash: --%";
            }

            if (activeWeekly != null)
            {
                PillWeeklyText.Text = $"周: {activeWeekly.Percentage:F1}%";
            }

            if (active5h != null)
            {
                var brush = GetStatusBrush(active5h.Percentage);
                PillStatusDot.Fill = brush;
                PillStatusGlow.Color = brush.Color;

                // 2. 低配额呼吸发光与边框警告变色 (低于 20% 变橙，低于 10% 变红且发光增强)
                if (active5h.Percentage < 10.0)
                {
                    PillStatusGlow.BlurRadius = 12;
                    PillStatusGlow.Opacity = 1.0;
                    PillBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0xEF, 0x44, 0x44));
                }
                else if (active5h.Percentage < 20.0)
                {
                    PillStatusGlow.BlurRadius = 8;
                    PillStatusGlow.Opacity = 0.8;
                    PillBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B));
                }
                else
                {
                    PillStatusGlow.BlurRadius = 6;
                    PillStatusGlow.Opacity = 0.6;
                    PillBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCB, 0xD5, 0xE1));
                }

                // 3. 满血恢复托盘气泡通知
                if (_config.RecoveryNotifyEnabled && _notifyIcon != null)
                {
                    if (_lastNotifiedGeminiPct < 85.0 && active5h.Percentage >= 99.0)
                    {
                        string modelTitle = isClaude ? "Claude Sonnet 4.6" : "Gemini 3.8 Flash";
                        _notifyIcon.ShowBalloonTip(3500, "⚡ 配额已满血恢复", $"{modelTitle} 5小时滚动配额已完全恢复，可继续全速编程！", Forms.ToolTipIcon.Info);
                    }
                }
                _lastNotifiedGeminiPct = active5h.Percentage;
            }

            // 2. 鼠标悬停 ToolTip 渲染 (清晰展示两大模型组的 5小时 与 周限额)
            TipUserText.Text = $"👤 {_lastStatus.UserName} · {_lastStatus.PlanName} 套餐";

            TipGroupsContainer.Children.Clear();
            foreach (var group in _lastStatus.Groups)
            {
                var groupBox = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x06, 0x00, 0x00, 0x00)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var sp = new StackPanel();

                string groupIcon = group.DisplayName.Contains("Gemini") ? "⚡" : "🔮";
                string groupTitle = group.DisplayName.Contains("Gemini") ? "Gemini 模型组 (3.8 Flash / Pro)" : "Claude & GPT 模型组 (Sonnet / Opus / GPT-OSS)";

                var lblGroup = new TextBlock
                {
                    Text = $"{groupIcon} {groupTitle}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                sp.Children.Add(lblGroup);

                // 5小时限额行
                if (group.FiveHourBucket != null)
                {
                    var b = group.FiveHourBucket;
                    sp.Children.Add(CreateBucketRow("5小时滚动", b.Percentage, b.ResetSeconds, is5h: true));
                }

                // 周限额行
                if (group.WeeklyBucket != null)
                {
                    var b = group.WeeklyBucket;
                    sp.Children.Add(CreateBucketRow("本周额度", b.Percentage, b.ResetSeconds, is5h: false));
                }

                groupBox.Child = sp;
                TipGroupsContainer.Children.Add(groupBox);
            }

            // 3. 详情板 (DetailsCard) 配额与模型渲染
            DetailsUserText.Text = $"👤 {_lastStatus.UserName} · {_lastStatus.PlanName} 套餐";

            DetailsQuotaContainer.Children.Clear();
            foreach (var group in _lastStatus.Groups)
            {
                var groupBox = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x04, 0x00, 0x00, 0x00)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 5)
                };

                var sp = new StackPanel();
                string groupIcon = group.DisplayName.Contains("Gemini") ? "⚡" : "🔮";
                string groupTitle = group.DisplayName.Contains("Gemini") ? "Gemini 模型组 (Flash / Pro)" : "Claude & GPT 模型组 (Sonnet / Opus / GPT-OSS)";

                var lblGroup = new TextBlock
                {
                    Text = $"{groupIcon} {groupTitle}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B)),
                    Margin = new Thickness(0, 0, 0, 3)
                };
                sp.Children.Add(lblGroup);

                if (group.FiveHourBucket != null)
                {
                    var b = group.FiveHourBucket;
                    sp.Children.Add(CreateBucketRow("5小时滚动", b.Percentage, b.ResetSeconds, is5h: true));
                }

                if (group.WeeklyBucket != null)
                {
                    var b = group.WeeklyBucket;
                    sp.Children.Add(CreateBucketRow("本周额度", b.Percentage, b.ResetSeconds, is5h: false));
                }

                groupBox.Child = sp;
                DetailsQuotaContainer.Children.Add(groupBox);
            }

            // 4. 渲染可用模型清单 (AvailableModels)
            ModelCountText.Text = _lastStatus.AvailableModels.Count.ToString();
            DetailsModelsContainer.Children.Clear();

            if (_lastStatus.AvailableModels.Count == 0)
            {
                DetailsModelsContainer.Children.Add(new TextBlock
                {
                    Text = "暂无可用模型数据",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8)),
                    Margin = new Thickness(4, 8, 4, 8),
                    HorizontalAlignment = WpfHorizontalAlignment.Center
                });
            }
            else
            {
                int GetTierRank(string label)
                {
                    if (label.Contains("High", StringComparison.OrdinalIgnoreCase)) return 1;
                    if (label.Contains("Medium", StringComparison.OrdinalIgnoreCase)) return 2;
                    if (label.Contains("Low", StringComparison.OrdinalIgnoreCase)) return 3;
                    return 4;
                }

                // 智能排序：最新代版本置顶 (3.8 > 3.7 > 3.6 > 3.1)，同代按档位 (High > Medium > Low) 排序
                var geminiModels = _lastStatus.AvailableModels
                    .Where(m => m.Category == "Gemini")
                    .OrderByDescending(m => m.Version)
                    .ThenBy(m => GetTierRank(m.Label))
                    .ThenBy(m => m.Label)
                    .ToList();

                var otherModels = _lastStatus.AvailableModels
                    .Where(m => m.Category != "Gemini")
                    .OrderByDescending(m => m.Version)
                    .ThenBy(m => m.Label.Contains("Sonnet") ? 0 : m.Label.Contains("Opus") ? 1 : 2)
                    .ThenBy(m => GetTierRank(m.Label))
                    .ToList();

                if (geminiModels.Count > 0)
                {
                    DetailsModelsContainer.Children.Add(new TextBlock
                    {
                        Text = "⚡ Google Gemini 系列",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69)),
                        Margin = new Thickness(2, 4, 2, 2)
                    });

                    foreach (var m in geminiModels)
                    {
                        DetailsModelsContainer.Children.Add(CreateModelItemRow(m));
                    }
                }

                if (otherModels.Count > 0)
                {
                    DetailsModelsContainer.Children.Add(new TextBlock
                    {
                        Text = "🔮 Claude & 第三方系列",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x3A, 0xED)),
                        Margin = new Thickness(2, 6, 2, 2)
                    });

                    foreach (var m in otherModels)
                    {
                        DetailsModelsContainer.Children.Add(CreateModelItemRow(m));
                    }
                }
            }

            // 更新托盘提示文字
            if (_notifyIcon != null && active5h != null && activeWeekly != null)
            {
                string tag = isClaude ? "Claude" : "Gemini";
                _notifyIcon.Text = $"Antigravity ({tag}): 5h {active5h.Percentage:F1}% | 周 {activeWeekly.Percentage:F1}%";
            }

            UpdateCountdownDisplay();
        }

        private UIElement CreateModelItemRow(ModelConfigItem model)
        {
            var itemBorder = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x05, 0x00, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 1, 0, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"点击复制: {model.Label}"
            };

            itemBorder.MouseEnter += (s, e) =>
            {
                itemBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xFD, 0xFA));
                itemBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA7, 0xF3, 0xD0));
            };
            itemBorder.MouseLeave += (s, e) =>
            {
                itemBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x05, 0x00, 0x00, 0x00));
                itemBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9));
            };

            itemBorder.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    System.Windows.Clipboard.SetText(model.Label);
                    ShowToast($"✓ 已复制: {model.Label}");
                }
                catch { }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = model.Category == "Gemini" ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var lbl = new TextBlock
            {
                Text = model.Label,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            leftSp.Children.Add(dot);
            leftSp.Children.Add(lbl);
            Grid.SetColumn(leftSp, 0);

            var rightSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (model.IsLatest)
            {
                var latestBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xED, 0xD5)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(3, 0, 0, 0)
                };
                latestBorder.Child = new TextBlock
                {
                    Text = "🔥 最新",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC2, 0x41, 0x0C))
                };
                rightSp.Children.Add(latestBorder);
            }
            if (!string.IsNullOrEmpty(model.TagTitle))
            {
                var tagBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(3, 0, 0, 0)
                };
                tagBorder.Child = new TextBlock
                {
                    Text = model.TagTitle,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D))
                };
                rightSp.Children.Add(tagBorder);
            }
            if (model.Label.Contains("Thinking", StringComparison.OrdinalIgnoreCase))
            {
                var thinkBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xE8, 0xFF)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(3, 0, 0, 0)
                };
                thinkBorder.Child = new TextBlock
                {
                    Text = "Thinking",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7E, 0x22, 0xCE))
                };
                rightSp.Children.Add(thinkBorder);
            }

            Grid.SetColumn(rightSp, 1);
            grid.Children.Add(leftSp);
            grid.Children.Add(rightSp);

            itemBorder.Child = grid;
            return itemBorder;
        }

        private Grid CreateBucketRow(string label, double pct, int resetSecs, bool is5h)
        {
            var brush = GetStatusBrush(pct);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);

            var pb = new WpfProgressBar
            {
                Height = 4,
                Value = pct,
                Maximum = 100,
                Foreground = brush,
                Style = (Style)FindResource("LightProgressBarStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(pb, 1);

            var val = new TextBlock
            {
                Text = $"{pct:F1}%",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                HorizontalAlignment = WpfHorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(val, 2);

            row.Children.Add(lbl);
            row.Children.Add(pb);
            row.Children.Add(val);

            return row;
        }

        private void UpdateCountdownDisplay()
        {
            if (_lastStatus == null || !_lastStatus.Success) return;
            bool isClaude = _config.MainDisplayMode == "Claude";
            var activeGroup = isClaude ? (_lastStatus.ClaudeGptGroup ?? _lastStatus.GeminiGroup) : _lastStatus.GeminiGroup;
            var active5h = activeGroup?.FiveHourBucket;
            if (active5h == null) return;

            int elapsed = (int)(DateTime.UtcNow - _lastFetchTime).TotalSeconds;
            int remSecs = Math.Max(0, active5h.ResetSeconds - elapsed);

            string formatted = FormatTime(remSecs);
            PillCountdownText.Text = formatted;
        }

        private string FormatTime(int secs)
        {
            if (secs <= 0) return "已重置";
            int d = secs / 86400;
            int h = (secs % 86400) / 3600;
            int m = (secs % 3600) / 60;
            int s = secs % 60;

            if (d > 0) return $"{d}天{h:D2}时";
            return h > 0 ? $"{h:D2}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
        }
    }
}
