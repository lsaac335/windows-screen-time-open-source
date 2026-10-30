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
    private readonly List<WidgetAppRow> _appRows = new();
    private bool _allowClose, _placed, _rendering, _modeChanging;
    private bool? _lastPinned;
    private bool _dragPending, _dragging;
    private Point _dragStartScreen, _dragStartWindow;
    private string _modeText = "桌面浮窗", _modeDetail = "普通窗口可以覆盖组件。点击任务栏入口或托盘的“显示小组件”可重新唤出。";

    public event Action? OpenDashboardRequested;
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
                _modeDetail = "普通窗口可以覆盖组件。点击任务栏入口或托盘的“显示小组件”可重新唤出。";
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
    }

    private void Render()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            ThemeManager.Apply(this, _controller.DarkMode);
            Background = Brushes.Transparent;
            var day = _controller.GetDay(_controller.Today);
            WidgetDate.Text = _controller.Today.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
            WidgetScreen.Text = day.HasData ? ShortDuration(day.ScreenSeconds) : "—";
            WidgetForeground.Text = day.HasData ? ShortDuration(day.ForegroundSeconds) : "—";
            WidgetScreen.ToolTip = "今日亮屏：" + MainWindow.FormatDuration(day.ScreenSeconds);
            WidgetForeground.ToolTip = "活跃时长：" + MainWindow.FormatDuration(day.ForegroundSeconds) + "，按记录时的空闲规则估算，不含本软件。";
            WidgetRawForeground.Text = day.HasData ? "前台 " + (day.HasLegacyActivity ? "≥ " : "") + ShortDuration(day.RawForegroundSeconds) : "前台 —";
            WidgetRawForeground.ToolTip = "前台时长：" + MainWindow.FormatForegroundDuration(day.RawForegroundSeconds, day.HasLegacyActivity) + "，包含空闲，不含本软件。" +
                (day.HasLegacyActivity ? "\n" + MainWindow.LegacyActivityExplanation : "");
            AutomationProperties.SetName(WidgetRawForeground, WidgetRawForeground.ToolTip.ToString());
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
        public readonly Grid Root = new() { Height = 23 };
        public readonly Border Icon = new() { Width = 17, Height = 17, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        public readonly TextBlock Name = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 10, 0) };
        public readonly TextBlock Duration = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        public string? AppId;
        public ImageSource? Source;
        public WidgetAppRow()
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Name, 1); Grid.SetColumn(Duration, 2);
            Root.Children.Add(Icon); Root.Children.Add(Name); Root.Children.Add(Duration);
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
            row.Name.Text = app.Name; row.Name.Foreground = ThemeManager.GetBrush("Ink"); row.Name.ToolTip = app.Name;
            row.Duration.Text = ShortDuration(app.Seconds); row.Duration.Foreground = ThemeManager.GetBrush("Muted");
            row.Root.ToolTip = app.Name + "\n活跃 " + MainWindow.FormatDuration(app.Seconds) + "\n前台 " + MainWindow.FormatForegroundDuration(app.ForegroundSeconds, hasLegacyActivity) +
                (hasLegacyActivity ? "\n" + MainWindow.LegacyActivityExplanation : "");
            var source = _controller.GetAppIcon(app.AppId);
            if (row.AppId != app.AppId || row.Source != source || source == null)
            {
                row.AppId = app.AppId; row.Source = source;
                row.Icon.Background = ThemeManager.GetBrush("AccentSoft");
                row.Icon.Child = source != null ? new Image { Source = source, Width = 16, Height = 16 } : new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(app.Name) ? "?" : app.Name.Substring(0, 1), FontSize = 10,
                    Foreground = ThemeManager.GetBrush("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
            }
        }
    }

    private static string ShortDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration.TotalHours >= 1) return (int)duration.TotalHours + "时" + duration.Minutes.ToString("00") + "分";
        if (duration.TotalMinutes >= 1) return (int)duration.TotalMinutes + "分" + duration.Seconds.ToString("00") + "秒";
        return (int)duration.TotalSeconds + "秒";
    }

    private void CreateMenu()
    {
        _menu.Style = (Style)FindResource("WidgetMenu");
        _menu.Resources.MergedDictionaries.Add(Resources);
        var open = new MenuItem { Header = "打开完整面板" }; open.Click += (_, _) => OpenDashboardRequested?.Invoke();
        _lockedMenu.Click += (_, _) => Safe(() => _controller.WidgetLocked = !_controller.WidgetLocked);
        _pinnedMenu.Click += (_, _) => Safe(() => _controller.WidgetPinned = !_controller.WidgetPinned);
        _darkMenu.Click += (_, _) => Safe(() => _controller.DarkMode = !_controller.DarkMode);
        var hide = new MenuItem { Header = "隐藏组件" }; hide.Click += (_, _) => Safe(HideWidget);
        _menu.Items.Add(open); _menu.Items.Add(new Separator()); _menu.Items.Add(_lockedMenu); _menu.Items.Add(_pinnedMenu); _menu.Items.Add(_darkMenu);
        _menu.Items.Add(new Separator()); _menu.Items.Add(hide);
        foreach (var item in _menu.Items.OfType<MenuItem>()) item.Style = (Style)FindResource("WidgetMenuItem");
        foreach (var separator in _menu.Items.OfType<Separator>()) separator.Style = (Style)FindResource("WidgetMenuSeparator");
    }

    private void RefreshMenu()
    {
        _lockedMenu.IsChecked = _controller.WidgetLocked;
        _pinnedMenu.IsChecked = _controller.WidgetPinned;
        _darkMenu.IsChecked = _controller.DarkMode;
        _menu.Background = ThemeManager.GetBrush("CardBackground"); _menu.Foreground = ThemeManager.GetBrush("Ink"); _menu.BorderBrush = ThemeManager.GetBrush("Line");
        foreach (var item in _menu.Items.OfType<MenuItem>()) { item.Foreground = ThemeManager.GetBrush("Ink"); item.Background = Brushes.Transparent; }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    { RefreshMenu(); _menu.PlacementTarget = MoreButton; _menu.Placement = PlacementMode.Bottom; _menu.HorizontalOffset = MoreButton.ActualWidth - 184; _menu.VerticalOffset = 4; _menu.IsOpen = true; }
    private void OpenDashboard_Click(object sender, RoutedEventArgs e) => Safe(() => OpenDashboardRequested?.Invoke());
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
        var dpi = VisualTreeHelper.GetDpi(this);
        var primary = Forms.Screen.PrimaryScreen;
        return Forms.Screen.AllScreens.OrderBy(s => s.DeviceName != primary?.DeviceName).Select(s =>
            new Rect(s.WorkingArea.Left / dpi.DpiScaleX, s.WorkingArea.Top / dpi.DpiScaleY,
                s.WorkingArea.Width / dpi.DpiScaleX, s.WorkingArea.Height / dpi.DpiScaleY)).ToList();
    }

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
