using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenTime;

public partial class WidgetWindow : Window
{
    private readonly IAppController _controller;
    private readonly ContextMenu _menu = new();
    private readonly MenuItem _lockedMenu = new() { Header = "锁定位置", IsCheckable = true };
    private readonly MenuItem _pinnedMenu = new() { Header = "浮动置顶", IsCheckable = true };
    private readonly MenuItem _darkMenu = new() { Header = "深色模式", IsCheckable = true };
    private readonly MenuItem _lightMenu = new() { Header = "浅色模式", IsCheckable = true };
    private readonly MenuItem _systemThemeMenu = new() { Header = "跟随系统主题", IsCheckable = true };
    private readonly List<WidgetAppRow> _appRows = new();
    private bool _allowClose, _placed, _rendering, _modeChanging;
    private bool? _lastPinned;
    private bool _dragPending, _dragging;
    private Point _dragStartScreen, _dragStartWindow;
    private string _modeText = "桌面浮窗", _modeDetail = "普通窗口可以覆盖组件。点击任务栏入口或托盘的“显示小组件”可重新唤出。";

    public event Action? OpenDashboardRequested;
    public event Action? BackgroundRequested;
    public string ModeDetail => _modeDetail;
    public string ModeName => _modeText;

    public WidgetWindow(IAppController controller)
    {
        _controller = controller;
        InitializeComponent();
        CreateMenu();
        WidgetCard.ContextMenu = _menu;
        _menu.Opened += (_, _) => RefreshMenu();
        _controller.Updated += Controller_Updated;
        Closing += Widget_Closing;
        Closed += (_, _) => { _controller.Updated -= Controller_Updated; DesktopWidgetHost.Detach(this); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); else CancelDrag(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && !_dragPending) ClampPosition(); }));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Q && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            { e.Handled = true; _controller.Exit(); }
        };
        Render();
    }

    public void ShowWidget(bool reveal = false)
    {
        DesktopWidgetHost.Detach(this);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        if (!_placed)
        {
            _placed = true;
            var area = GetWorkingAreas().FirstOrDefault();
            var left = _controller.WidgetLeft ?? area.Right - Width - 24;
            var top = _controller.WidgetTop ?? area.Top + 24;
            MoveInsideWorkingArea(left, top);
        }
        else ClampPosition();
        RefreshMode();
        Render();
        if (reveal)
        {
            // A deliberate show action must reveal an already enabled widget as well.
            // Raising it does not persist a different always-on-top preference.
            Topmost = true;
            Activate();
            Topmost = _controller.WidgetPinned;
        }
    }

    public void PrepareForExit()
    {
        _allowClose = true;
        CancelDrag();
        DesktopWidgetHost.Detach(this);
    }

    public void RefreshMode()
    {
        if (_modeChanging || !IsVisible) return;
        _modeChanging = true;
        try
        {
            // Layered WPF child windows can report successful desktop parenting without
            // rendering visibly. Keep the proven top-level view until that path is verified.
            DesktopWidgetHost.Detach(this);
            if (_controller.WidgetPinned)
            {
                Topmost = true;
                _modeText = "悬浮置顶";
                _modeDetail = "组件悬浮在其他窗口上方；右键取消“浮动置顶”可恢复普通组件窗口。";
            }
            else
            {
                Topmost = false;
                _modeText = "桌面浮窗";
                _modeDetail = "普通窗口可以覆盖组件。组件不显示任务栏图标；可从托盘“显示小组件”重新唤出。";
            }
            _lastPinned = _controller.WidgetPinned;
            ClampPosition();
            WidgetMode.Text = _modeText;
            WidgetMode.ToolTip = _modeDetail;
        }
        finally { _modeChanging = false; }
    }

    private void Controller_Updated()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Controller_Updated)); return; }
        if (!IsVisible) return;
        if (_lastPinned != _controller.WidgetPinned)
            RefreshMode();
        Render();
        if (_menu.IsOpen) RefreshMenu();
    }

    private void Render()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            ThemeManager.Apply(this, _controller.DarkMode);
            var day = _controller.GetDay(_controller.Today);
            WidgetDate.Text = _controller.Today.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
            WidgetScreen.Text = day.HasData ? ShortDuration(day.ScreenSeconds) : "—";
            DurationTypography.Set(WidgetForeground, day.HasData ? ShortDuration(day.ForegroundSeconds) : "—");
            double? pct = day.HasData && day.ScreenSeconds > 0 ? Math.Clamp(day.ForegroundSeconds / day.ScreenSeconds, 0, 1) : null;
            WidgetPctText.Text = pct.HasValue ? pct.Value.ToString("P0", CultureInfo.GetCultureInfo("zh-CN")) : "—";
            string heroTip = day.HasData
                ? $"今日活跃 {MainWindow.FormatDuration(day.ForegroundSeconds)}\n亮屏 {MainWindow.FormatDuration(day.ScreenSeconds)} · 前台 {MainWindow.FormatForegroundDuration(day.RawForegroundSeconds, day.HasLegacyActivity)}"
                : "开始记录后显示今日统计";
            WidgetForeground.ToolTip = heroTip;
            WidgetPctBadge.ToolTip = heroTip;
            AutomationProperties.SetName(WidgetPctBadge, "活跃时长占亮屏时长 " + WidgetPctText.Text);
            WidgetRawForeground.Text = day.HasData ? "前台 " + (day.HasLegacyActivity ? "≥ " : "") + ShortDuration(day.RawForegroundSeconds) : "前台 —";
            WidgetChart.MetricLabel = "活跃";
            WidgetChart.MetricIsLowerBound = false;
            WidgetChart.SetData(day.ScreenHourly, day.ForegroundHourly, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(), day.HasData, "开始后显示实际记录");
            var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.Now, _controller.TimeZoneId).DateTime;
            WidgetChart.SetCoverage(day.ObservedHourly, day.UnknownHourly,
                Enumerable.Range(0, 24).Select(h => h > now.Hour).ToArray(),
                Enumerable.Range(0, 24).Select(h => Math.Clamp(now.TimeOfDay.TotalSeconds - h * 3600, 0, 3600)).ToArray());
            UpdateAppRows(day.Apps.OrderByDescending(a => a.Seconds).Take(2).ToList(), day.HasLegacyActivity);
            bool paused = _controller.IsPaused;
            bool warning = _controller.HasError;
            WidgetStatus.Text = !_controller.IsRecordingEnabled ? "等待开始" : warning ? "采集异常" : paused ? "已暂停" : _controller.StatusText;
            WidgetStatus.ToolTip = _controller.StatusText + "\n" + _controller.CurrentAppName + " · " + _controller.DisplayStateText;
            WidgetStatusDot.Fill = ThemeManager.GetBrush(warning ? "StatusWarning" : paused ? "Secondary" : "StatusGood");
            WidgetPause.Content = !_controller.IsRecordingEnabled ? "开始" : paused ? "继续" : "暂停";
            string pauseAction = !_controller.IsRecordingEnabled ? "开始记录" : paused ? "恢复记录" : "暂停记录";
            WidgetPause.ToolTip = pauseAction;
            AutomationProperties.SetName(WidgetPause, pauseAction);
            WidgetMode.Text = _modeText;
            WidgetMode.ToolTip = _modeDetail;
            LockGlyph.Visibility = _controller.WidgetLocked ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            WidgetStatus.Text = "读取记录异常";
            WidgetStatus.ToolTip = ex.Message;
            WidgetStatusDot.Fill = ThemeManager.GetBrush("StatusWarning");
        }
        finally { _rendering = false; }
    }

    private sealed class WidgetAppRow
    {
        private static readonly string[] FallbackColors = { "#6373F2", "#34B89A", "#E5A04B", "#4A9CE8", "#D46E9E", "#7C8AA6" };
        public readonly Grid Root = new() { Height = 26 };
        public readonly Border Icon = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        public readonly TextBlock Name = new() { FontSize = 13, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
        public readonly TextBlock Duration = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        public string? AppId;
        public string? FallbackName;
        public ImageSource? Source;

        public WidgetAppRow()
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Name, 1);
            Grid.SetColumn(Duration, 2);
            System.Windows.Documents.Typography.SetNumeralAlignment(Duration, FontNumeralAlignment.Tabular);
            Root.Children.Add(Icon); Root.Children.Add(Name); Root.Children.Add(Duration);
        }

        public static string FallbackColor(string key)
        {
            unchecked { int h = 17; foreach (char c in key) h = h * 31 + c; return FallbackColors[(int)((uint)h % FallbackColors.Length)]; }
        }
    }

    private void UpdateAppRows(List<AppUsage> apps, bool hasLegacyActivity)
    {
        WidgetEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        while (_appRows.Count < apps.Count)
        {
            var row = new WidgetAppRow(); _appRows.Add(row); WidgetApps.Children.Add(row.Root);
        }
        for (int i = 0; i < _appRows.Count; i++)
        {
            var row = _appRows[i]; row.Root.Visibility = i < apps.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i >= apps.Count) continue;
            var app = apps[i];
            string name = string.IsNullOrWhiteSpace(app.Name) ? "未识别应用" : app.Name;
            row.Name.Text = name; row.Name.Foreground = ThemeManager.GetBrush("Ink"); row.Name.ToolTip = name;
            row.Duration.Text = ShortDuration(app.Seconds); row.Duration.Foreground = ThemeManager.GetBrush("Muted");
            row.Root.ToolTip = name + "\n活跃 " + MainWindow.FormatDuration(app.Seconds) + "\n前台 " + MainWindow.FormatForegroundDuration(app.ForegroundSeconds, hasLegacyActivity) +
                (hasLegacyActivity ? "\n" + MainWindow.LegacyActivityExplanation : "");
            ImageSource? source = null;
            try { source = _controller.GetAppIcon(app.AppId); } catch { }
            if (source != null)
            {
                if (row.AppId != app.AppId || !ReferenceEquals(row.Source, source))
                {
                    row.AppId = app.AppId; row.Source = source; row.FallbackName = null;
                    row.Icon.Background = Brushes.Transparent;
                    row.Icon.Child = new Image { Source = source, Width = 17, Height = 17, Stretch = Stretch.Uniform };
                }
            }
            else if (row.AppId != app.AppId || row.Source != null || row.FallbackName != name)
            {
                row.AppId = app.AppId; row.Source = null; row.FallbackName = name;
                row.Icon.Background = ThemeManager.LegacyBrush(WidgetAppRow.FallbackColor(app.AppId ?? name));
                row.Icon.Child = new TextBlock
                {
                    Text = name.Substring(0, Math.Min(name.Length, name[0] <= 127 ? 2 : 1)).ToUpperInvariant(),
                    FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
            }
        }
    }

    private static string ShortDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration.TotalHours >= 1)
            return duration.Minutes == 0 ? (int)duration.TotalHours + "时" : (int)duration.TotalHours + "时" + duration.Minutes.ToString("00") + "分";
        if (duration.TotalMinutes >= 1)
            return duration.Seconds == 0 ? (int)duration.TotalMinutes + "分" : (int)duration.TotalMinutes + "分" + duration.Seconds.ToString("00") + "秒";
        return (int)duration.TotalSeconds + "秒";
    }

    private void CreateMenu()
    {
        _menu.Style = (Style)FindResource("WidgetMenu");
        _menu.Resources.MergedDictionaries.Add(Resources);
        var open = new MenuItem { Header = "打开完整面板" }; open.Click += (_, _) => OpenDashboardRequested?.Invoke();
        var details = new MenuItem { Header = "查看今日数据明细" }; details.Click += ChartDetails_Click;
        var background = new MenuItem { Header = "隐藏窗口，后台记录" }; background.Click += (_, _) => BackgroundRequested?.Invoke();
        _lockedMenu.Click += (_, _) => Safe(() => _controller.WidgetLocked = !_controller.WidgetLocked);
        _pinnedMenu.Click += (_, _) => Safe(() => _controller.WidgetPinned = !_controller.WidgetPinned);
        _darkMenu.Click += (_, _) => Safe(() => _controller.DarkMode = true);
        _lightMenu.Click += (_, _) => Safe(() => _controller.DarkMode = false);
        _systemThemeMenu.Click += (_, _) => Safe(() => _controller.FollowSystemTheme = true);
        var hide = new MenuItem { Header = "隐藏组件" }; hide.Click += (_, _) => Safe(HideWidget);
        _menu.Items.Add(open); _menu.Items.Add(details); _menu.Items.Add(new Separator()); _menu.Items.Add(_lockedMenu); _menu.Items.Add(_pinnedMenu);
        _menu.Items.Add(new Separator()); _menu.Items.Add(_systemThemeMenu); _menu.Items.Add(_lightMenu); _menu.Items.Add(_darkMenu);
        _menu.Items.Add(new Separator()); _menu.Items.Add(background); _menu.Items.Add(hide);
        foreach (var item in _menu.Items.OfType<MenuItem>()) item.Style = (Style)FindResource("WidgetMenuItem");
        foreach (var separator in _menu.Items.OfType<Separator>()) separator.Style = (Style)FindResource("WidgetMenuSeparator");
    }

    private void RefreshMenu()
    {
        _lockedMenu.IsChecked = _controller.WidgetLocked;
        _pinnedMenu.IsChecked = _controller.WidgetPinned;
        _systemThemeMenu.IsChecked = _controller.FollowSystemTheme;
        _darkMenu.IsChecked = !_controller.FollowSystemTheme && _controller.DarkMode;
        _lightMenu.IsChecked = !_controller.FollowSystemTheme && !_controller.DarkMode;
        _menu.Background = ThemeManager.GetBrush("CardBackground"); _menu.Foreground = ThemeManager.GetBrush("Ink"); _menu.BorderBrush = ThemeManager.GetBrush("Line");
        foreach (var item in _menu.Items.OfType<MenuItem>()) { item.Foreground = ThemeManager.GetBrush("Ink"); item.Background = Brushes.Transparent; }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    { RefreshMenu(); _menu.PlacementTarget = MoreButton; _menu.Placement = PlacementMode.Bottom; _menu.HorizontalOffset = MoreButton.ActualWidth - 184; _menu.VerticalOffset = 4; _menu.IsOpen = true; }
    private void OpenDashboard_Click(object sender, RoutedEventArgs e) => Safe(() => OpenDashboardRequested?.Invoke());
    private void ChartDetails_Click(object sender, RoutedEventArgs e) => Safe(() => WidgetChart.ShowDetails("今日 · 亮屏与活跃明细"));
    private void Pause_Click(object sender, RoutedEventArgs e) => Safe(() => { if (!_controller.IsRecordingEnabled) _controller.StartRecording(); else _controller.TogglePause(); });
    private void HideWidget() { _controller.WidgetEnabled = false; Hide(); }
    private void Widget_Closing(object? sender, CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; Safe(HideWidget); } }
    private void Safe(Action action)
    {
        try { action(); Render(); }
        catch (Exception ex) { WidgetStatus.Text = "操作未完成"; WidgetStatus.ToolTip = ex.Message; WidgetStatusDot.Fill = ThemeManager.GetBrush("StatusWarning"); }
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase || source is MenuItem) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private Point ScreenPoint(MouseEventArgs e)
    {
        var pixels = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) { CancelDrag(); Safe(() => OpenDashboardRequested?.Invoke()); e.Handled = true; return; }
        if (_controller.WidgetLocked) return;
        var position = DesktopWidgetHost.GetScreenPosition(this);
        _dragStartWindow = new Point(position.Left, position.Top); _dragStartScreen = ScreenPoint(e);
        _dragPending = true; _dragging = false;
        WidgetCard.CaptureMouse(); e.Handled = true;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
        var delta = ScreenPoint(e) - _dragStartScreen;
        if (!_dragging && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        DesktopWidgetHost.MoveToScreen(this, _dragStartWindow.X + delta.X, _dragStartWindow.Y + delta.Y);
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragPending) return;
        bool save = _dragging; CancelDrag(); e.Handled = true;
        if (save) Safe(() => { ClampPosition(); var point = DesktopWidgetHost.GetScreenPosition(this); _controller.SaveWidgetPosition(point.Left, point.Top); });
    }

    private void Card_LostMouseCapture(object sender, MouseEventArgs e) { _dragPending = false; _dragging = false; }
    private void CancelDrag() { _dragPending = false; _dragging = false; if (WidgetCard.IsMouseCaptured) WidgetCard.ReleaseMouseCapture(); }

    private List<Rect> GetWorkingAreas()
    {
        var fallbackDpi = VisualTreeHelper.GetDpi(this);
        var primary = Forms.Screen.PrimaryScreen;
        return Forms.Screen.AllScreens.OrderBy(s => s.DeviceName != primary?.DeviceName).Select(s =>
        {
            var scale = GetMonitorScale(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2, fallbackDpi);
            return new Rect(s.WorkingArea.Left / scale.X, s.WorkingArea.Top / scale.Y,
                s.WorkingArea.Width / scale.X, s.WorkingArea.Height / scale.Y);
        }).ToList();
    }

    private static (double X, double Y) GetMonitorScale(int px, int py, DpiScale fallback)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = px, Y = py }, 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return (dpiX / 96d, dpiY / 96d);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return (fallback.DpiScaleX, fallback.DpiScaleY);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);
    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private void ClampPosition()
    {
        var position = DesktopWidgetHost.GetScreenPosition(this);
        MoveInsideWorkingArea(position.Left, position.Top);
    }

    private void MoveInsideWorkingArea(double left, double top)
    {
        var areas = GetWorkingAreas();
        if (areas.Count == 0) areas.Add(SystemParameters.WorkArea);
        if (!double.IsFinite(left) || !double.IsFinite(top)) { left = areas[0].Right - Width - 24; top = areas[0].Top + 24; }
        var target = new Rect(left, top, Width, Height);
        var area = areas.OrderByDescending(a => { var intersection = Rect.Intersect(a, target); return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height; }).First();
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        DesktopWidgetHost.MoveToScreen(this, left, top);
    }
}
