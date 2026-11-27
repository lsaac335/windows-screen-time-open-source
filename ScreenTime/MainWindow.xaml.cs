using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Microsoft.Win32;

namespace ScreenTime
{
    public partial class MainWindow : Window
    {
        private readonly IAppController _controller;
        private DateTime _selectedDate;
        private DateTime _lastToday;
        private bool _followToday = true;
        private bool _isWeek;
        private bool _showRawForeground;
        private bool _showVisible;
        private bool _ready;
        private bool _rendering;
        private bool? _lastDarkMode;
        private string _page = "overview";
        private int _appPage;
        private string _lastAppSearch = "";
        private const int AppsPerPage = 25;
        private DaySummary? _summary;
        private List<DaySummary> _week = new List<DaySummary>();
        private DateTime? _cachedWeekEnd;
        private List<DaySummary>? _cachedWeekPrefix;
        private readonly Dictionary<StackPanel, Dictionary<string, AppRowView>> _rowCaches = new Dictionary<StackPanel, Dictionary<string, AppRowView>>();
        private static readonly string[] AppColors = { "#6373F2", "#34B89A", "#E5A04B", "#4A9CE8", "#D46E9E", "#7C8AA6" };
        public event Action? WidgetRequested;
        public event Action? BackgroundRequested;

        public MainWindow(IAppController controller)
        {
            _controller = controller;
            _selectedDate = _lastToday = controller.Today;
            InitializeComponent();
            ConfigureNativeFrame();
            RestoreWindowBounds();
            AboutVersionText.Text = $"版本 {AppSettings.CurrentVersion} · 开源许可：MIT";
            FooterVersionText.Text = $"屏幕时间 {AppSettings.CurrentVersion} · 本地优先的 Windows 使用记录";
            AppsBarChart.AppSelected += app => ShowAppDetails(app, _selectedDate, _isWeek);
            ThemeManager.Apply(this, _controller.DarkMode);
            DateSelector.SelectedDate = _selectedDate;
            DateSelector.DisplayDateEnd = _lastToday;
            _ready = true;
            _controller.Updated += Controller_Updated;
            Loaded += (_, _) => Render();
            IsVisibleChanged += (_, _) => { if (IsVisible) Render(); };
            Closing += Window_Closing;
            Closed += (_, _) => _controller.Updated -= Controller_Updated;
            StateChanged += (_, _) => UpdateCaptionLayout();
            SizeChanged += (_, _) => UpdateCaptionLayout();
            LocationChanged += (_, _) => UpdateCaptionLayout();
            DpiChanged += (_, _) => UpdateCaptionLayout();
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Q && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; _controller.Exit(); } };
        }

        private void RestoreWindowBounds()
        {
            var workArea = SystemParameters.WorkArea;
            if (_controller.WindowWidth is double savedW && _controller.WindowHeight is double savedH &&
                savedW >= MinWidth && savedH >= MinHeight)
            {
                Width = Math.Min(savedW, workArea.Width);
                Height = Math.Min(savedH, workArea.Height);
            }
            else
            {
                Width = Math.Min(Width, workArea.Width);
                Height = Math.Min(Height, workArea.Height);
            }
            if (_controller.WindowLeft is double savedL && _controller.WindowTop is double savedT)
            {
                var virtualBounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (virtualBounds.Contains(new Point(savedL + 40, savedT + 40)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = savedL;
                    Top = savedT;
                }
            }
        }

        public void ShowAndActivate()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Render();
        }

        public void PrepareForExit() { }

        public void ShowDashboard()
        {
            _page = "overview";
            SelectDate(_controller.Today, false);
            ShowAndActivate();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // Persist user's preferred window size and placement when closing normally.
            if (WindowState == WindowState.Normal && double.IsFinite(ActualWidth) && double.IsFinite(ActualHeight))
                _controller.SaveWindowBounds(ActualWidth, ActualHeight, Left, Top);
        }

        private void Controller_Updated()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Controller_Updated)); return; }
            if (IsVisible) Render();
        }

        private void Render()
        {
            if (!_ready || _rendering) return;
            _rendering = true;
            try
            {
                ThemeManager.Apply(this, _controller.DarkMode);
                if (_lastDarkMode != _controller.DarkMode)
                {
                    _lastDarkMode = _controller.DarkMode;
                    _rowCaches.Clear(); TopAppsHost.Children.Clear(); AllAppsHost.Children.Clear();
                }
                Topmost = _controller.AlwaysOnTop;
                PinButton.IsChecked = _controller.AlwaysOnTop;
                PinButton.ToolTip = _controller.AlwaysOnTop ? "取消面板置顶" : "置顶面板";
                System.Windows.Automation.AutomationProperties.SetName(PinButton, _controller.AlwaysOnTop ? "取消面板置顶" : "置顶面板");
                DateTime today = _controller.Today;
                if (today != _lastToday)
                {
                    if (_followToday) _selectedDate = today;
                    _lastToday = today;
                }
                DateSelector.DisplayDateEnd = today;
                if (DateSelector.SelectedDate != _selectedDate) DateSelector.SelectedDate = _selectedDate;
                NextDateButton.IsEnabled = _selectedDate < today;
                bool enabled = _controller.IsRecordingEnabled;
                bool paused = _controller.IsPaused;
                string runtimeStatus = _controller.StatusText ?? "";
                bool warning = _controller.HasError || runtimeStatus.Contains("异常") || runtimeStatus.Contains("不支持");
                bool waiting = runtimeStatus.Contains("等待") || runtimeStatus.Contains("初始化") || runtimeStatus.Contains("未活动");
                bool neutral = !enabled || paused || waiting || runtimeStatus.Contains("空闲中") || runtimeStatus.Contains("休眠") || runtimeStatus.Contains("屏幕已关闭") || runtimeStatus.Contains("已停止");
                StatusLabel.Text = !enabled ? "尚未开始" : paused ? "已暂停" : _controller.HasError ? "采集异常" : string.IsNullOrEmpty(runtimeStatus) ? "等待状态" : runtimeStatus;
                var statusColor = Brush(warning ? "#C56E45" : neutral ? "#8B91A3" : "#318765");
                StatusLabel.Foreground = statusColor;
                StatusDot.Fill = statusColor;
                StatusPill.Background = Brush(warning ? "#FFF0E9" : neutral ? "#EDEFF4" : "#EAF6F0");
                PauseButton.Content = !enabled ? "开始" : paused ? "继续" : "暂停";
                CurrentAppText.Text = !enabled ? "等待开始" : paused ? "已暂停" : string.IsNullOrWhiteSpace(_controller.CurrentAppName) ? "等待前台应用" : _controller.CurrentAppName;
                DisplayStateText.Text = _controller.DisplayStateText;
                WelcomePanel.Visibility = !enabled && _page != "settings" ? Visibility.Visible : Visibility.Collapsed;
                ErrorBanner.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
                ErrorText.Text = warning ? runtimeStatus + "。当前受影响区间不会推算为正常使用时间。" : "";
                ForegroundRuleText.Text = _controller.IdleFilterEnabled
                    ? "活跃时长按键鼠操作" + (_controller.MediaDetectionEnabled ? "、媒体推测" : "") + (_controller.GamepadDetectionEnabled ? "、手柄输入" : "") + "估算；空闲阈值为 " + _controller.IdleTimeoutMinutes + " 分钟。前台时长包含空闲。"
                    : "空闲过滤已关闭：之后记录的活跃时长与前台时长相同，仍只统计获得焦点的应用。";
                PageHeading.Visibility = Visibility.Collapsed;
                PageTitle.Text = _page == "settings" ? "设置" : "应用统计";
                PageSubtitle.Text = _page == "settings" ? "记录、外观与数据管理" : RangeText();
                OverviewNav.IsChecked = _page == "overview";
                AppsNav.IsChecked = _page == "apps";
                SettingsNav.IsChecked = _page == "settings";
                OverviewPanel.Visibility = _page == "overview" ? Visibility.Visible : Visibility.Collapsed;
                AppsPanel.Visibility = _page == "apps" ? Visibility.Visible : Visibility.Collapsed;
                SettingsPanel.Visibility = _page == "settings" ? Visibility.Visible : Visibility.Collapsed;
                DateToolbar.Visibility = _page == "settings" ? Visibility.Collapsed : Visibility.Visible;
                if (_page == "settings")
                {
                    AutoStartCheck.IsChecked = _controller.AutoStart;
                    AutoRecordCheck.IsChecked = _controller.AutoRecord;
                    AutoStartWidgetCheck.IsChecked = _controller.AutoStartWidget;
                    IdleFilterCheck.IsChecked = _controller.IdleFilterEnabled;
                    MediaDetectionCheck.IsChecked = _controller.MediaDetectionEnabled;
                    GamepadDetectionCheck.IsChecked = _controller.GamepadDetectionEnabled;
                    VisibilityDetectionCheck.IsChecked = _controller.VisibilityDetectionEnabled;
                    IdleTimeoutSelector.SelectedValue = _controller.IdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
                    IdleTimeoutSelector.IsEnabled = _controller.IdleFilterEnabled;
                    IdleRuleDescription.Text = "开启后，连续 " + _controller.IdleTimeoutMinutes + " 分钟无键鼠操作的后续区间会排除；前 " + _controller.IdleTimeoutMinutes + " 分钟仍计入活跃。前台时长与亮屏时长不受影响。\n\n下方可用媒体播放和手柄输入补充活动依据。阅读仍可能被判为空闲，可在应用详情单独调整规则。修改只影响之后的记录。";
                    ThemeSelector.SelectedValue = _controller.FollowSystemTheme ? "system" : _controller.DarkMode ? "dark" : "light";
                    TopmostCheck.IsChecked = _controller.AlwaysOnTop;
                    WidgetModeCheck.IsChecked = _controller.WidgetEnabled;
                    DataPathText.Text = _controller.DataDirectory;
                    TimeZoneText.Text = "统计时区：" + _controller.TimeZoneId + "（固定，不随系统校时重排历史）";
                    SelectedRangeText.Text = "当前选定范围：" + RangeText() + "。删除历史不会停止之后的记录。";
                    return;
                }
                ActiveMetricButton.IsChecked = AppsActiveMetric.IsChecked = !_showRawForeground && !_showVisible;
                ForegroundMetricButton.IsChecked = AppsForegroundMetric.IsChecked = _showRawForeground;
                VisibleMetricButton.IsChecked = AppsVisibleMetric.IsChecked = _showVisible;
                TodayButton.IsChecked = !_isWeek && _selectedDate == today;
                YesterdayButton.IsChecked = !_isWeek && _selectedDate == today.AddDays(-1);
                WeekButton.IsChecked = _isWeek;

                if (_isWeek)
                {
                    _week = GetWeekCached(_selectedDate, today);
                    _summary = Combine(_week);
                }
                else _summary = _controller.GetDay(_selectedDate);
                var summary = _summary;
                MetricHintText.Text = _showVisible ? "允许多应用并行累计；表示展示，不代表关注" : _showRawForeground ? "包括空闲；只计获得焦点的应用" : "扣除超出阈值的空闲，是活跃估计值";
                if (_page == "apps") { RenderAllApps(summary.Apps); return; }
                HeroMetricHeading.Text = _isWeek ? "近 7 天活跃" : _selectedDate == today ? "今日活跃" : _selectedDate.ToString("M月d日") + "活跃";
                DurationTypography.Set(ScreenValue, summary.HasData ? FormatDuration(summary.ScreenSeconds) : "—");
                DurationTypography.Set(ForegroundValue, summary.HasData ? FormatDuration(summary.ForegroundSeconds) : "—");
                OverviewRing.SetUsage(summary.ForegroundSeconds, summary.ScreenSeconds, summary.HasData);
                UpdateTrendPill(summary);
                DurationTypography.Set(RawForegroundValue, summary.HasData ? FormatForegroundDuration(summary.RawForegroundSeconds, summary.HasLegacyActivity) : "—");
                LockedValue.Text = summary.HasData && summary.LockedSeconds >= 1 ? "含锁屏 " + FormatDuration(summary.LockedSeconds) : "";
                RawForegroundCaption.Text = summary.HasLegacyActivity ? "≥ 旧记录下限" : "含空闲";
                HeroSecondarySummary.Text = _controller.IdleFilterEnabled ? "已排除空闲 · 不含本软件" : "空闲过滤已关闭 · 不含本软件";
                TopAppsCaption.Text = "按" + MetricName + "时长排序";
                ChartMetricLegend.Text = "亮屏 / " + MetricName;
                MainChart.MetricLabel = MetricName;
                MainChart.MetricIsLowerBound = _showRawForeground && summary.HasLegacyActivity;
                bool chartHasData = summary.HasData || (_showVisible && summary.VisibilityObservedSeconds > 0);
                if (_isWeek)
                {
                    ChartTitle.Text = "最近七天的使用分布";
                    ChartCaption.Text = RangeText() + " · 亮屏与" + MetricName + "时长";
                    MainChart.SetPeriodNotes(BuildWeekPeriodNotes(_week));
                    MainChart.SetData(_week.Select(d => d.ScreenSeconds).ToArray(), _week.Select(MetricTotal).ToArray(),
                        _week.Select(d => DateTime.TryParse(d.Date, out var date) ? date.ToString("M/d") : d.Date).ToArray(), chartHasData);
                    SetWeekCoverage(MainChart, _week);
                    int valid = _week.Count(d => d.HasData);
                    CoverageText.Text = valid == 0 ? "这段时间还没有可用记录，灰纹表示未记录。" : "已记录 " + valid + "/7 天。灰纹为未记录或部分记录的日期，不能视为完整全天数据。";
                }
                else
                {
                    ChartTitle.Text = "分时使用";
                    ChartCaption.Text = _selectedDate.ToString("M 月 d 日") + " · 亮屏与" + MetricName + "时长";
                    bool detail = summary.HasHourlyDetails;
                    MainChart.SetPeriodNotes(detail ? BuildHourlyPeriodNotes(summary) : null);
                    MainChart.SetData(summary.ScreenHourly, _showVisible ? summary.VisibleHourly : _showRawForeground ? summary.RawForegroundHourly : summary.ForegroundHourly, Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToArray(), chartHasData && detail,
                        summary.HasData && !detail ? "小时明细已过保留期，仍可查看每日汇总" : _showVisible ? "此日期尚未采集可见时间，历史无法补算" : "还没有记录，开始后时间会在这里呈现");
                    if (detail) SetHourlyCoverage(MainChart, summary, _selectedDate);
                    else MainChart.SetCoverage(Array.Empty<double>(), Array.Empty<double>());
                    CoverageText.Text = !summary.HasData ? "无记录 · 开始记录后时间会在这里呈现" :
                        (summary.FirstRecorded.HasValue ? "自 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(summary.FirstRecorded.Value, _controller.TimeZoneId).ToString("HH:mm") + " 起记录 · 悬停柱条查看时段详情" : "悬停柱条查看各时段详情");
                }
                if (summary.HasLegacyActivity) CoverageText.Text += " " + LegacyActivityExplanation;
                if (_showVisible) CoverageText.Text = VisibilityExplanation + " 已采集窗口状态 " + FormatDuration(summary.VisibilityObservedSeconds) +
                    (summary.VisibilityFirstRecorded.HasValue ? "，本范围首次采集 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(summary.VisibilityFirstRecorded.Value, _controller.TimeZoneId).ToString("M/d HH:mm") : "；此范围暂无可见状态记录") + "。";
                if (_page == "overview") RenderTopApps(summary.Apps);
            }
            catch (Exception ex)
            {
                ErrorBanner.Visibility = Visibility.Visible;
                ErrorText.Text = "读取记录时遇到问题：" + ex.Message;
            }
            finally { _rendering = false; }
        }

        private List<DaySummary> GetWeekCached(DateTime endingDate, DateTime today)
        {
            if (_cachedWeekEnd == endingDate.Date && _cachedWeekPrefix != null && _cachedWeekPrefix.Count == 6)
            {
                var result = new List<DaySummary>(7);
                result.AddRange(_cachedWeekPrefix);
                result.Add(_controller.GetDay(endingDate));
                return result;
            }
            var full = _controller.GetWeek(endingDate);
            if (endingDate.Date == today.Date && full.Count == 7)
            {
                _cachedWeekEnd = endingDate.Date;
                _cachedWeekPrefix = full.Take(6).ToList();
            }
            else
            {
                _cachedWeekEnd = null;
                _cachedWeekPrefix = null;
            }
            return full;
        }

        private void InvalidateWeekCache() { _cachedWeekEnd = null; _cachedWeekPrefix = null; }

        private void UpdateTrendPill(DaySummary summary)
        {
            if (_isWeek || !summary.HasData || summary.ForegroundSeconds < 60)
            {
                TrendPill.Visibility = Visibility.Collapsed;
                return;
            }
            var prev = _controller.GetDay(_selectedDate.AddDays(-1));
            if (!prev.HasData || prev.ForegroundSeconds < 60)
            {
                TrendPill.Visibility = Visibility.Collapsed;
                return;
            }
            double delta = (summary.ForegroundSeconds - prev.ForegroundSeconds) / prev.ForegroundSeconds;
            int pct = (int)Math.Round(delta * 100);
            if (Math.Abs(pct) <= 2)
            {
                TrendText.Text = "与前一日持平";
                TrendPill.Background = ThemeManager.GetBrush("StatusIdleBackground");
                TrendText.Foreground = ThemeManager.GetBrush("Muted");
            }
            else if (pct > 0)
            {
                TrendText.Text = $"较前一日 +{pct}%";
                TrendPill.Background = ThemeManager.GetBrush("StatusWarningBackground");
                TrendText.Foreground = ThemeManager.GetBrush("StatusWarning");
            }
            else
            {
                TrendText.Text = $"较前一日 {pct}%";
                TrendPill.Background = ThemeManager.GetBrush("StatusGoodBackground");
                TrendText.Foreground = ThemeManager.GetBrush("StatusGood");
            }
            TrendPill.ToolTip = $"前一日（{_selectedDate.AddDays(-1):M/d}）活跃时长：{FormatDuration(prev.ForegroundSeconds)}";
            TrendPill.Visibility = Visibility.Visible;
        }

        private string[] BuildHourlyPeriodNotes(DaySummary summary)
        {
            var notes = new string[24];
            var map = _showVisible ? summary.AppVisibleHourly : _showRawForeground ? summary.AppForegroundHourly : summary.AppHourly;
            if (map == null || map.Count == 0) return notes;
            var names = summary.Apps.GroupBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.IsNullOrWhiteSpace(g.First().Name) ? "未识别应用" : g.First().Name, StringComparer.OrdinalIgnoreCase);
            for (int h = 0; h < 24; h++)
            {
                var top = map.Select(p => (Id: p.Key, Seconds: h < p.Value.Length ? p.Value[h] : 0))
                    .Where(x => x.Seconds >= 10)
                    .OrderByDescending(x => x.Seconds)
                    .Take(2)
                    .ToList();
                if (top.Count > 0)
                    notes[h] = "常用：" + string.Join(" · ", top.Select(t => (names.TryGetValue(t.Id, out var n) ? n : t.Id) + " " + FormatDuration(t.Seconds)));
            }
            return notes;
        }

        private string[] BuildWeekPeriodNotes(List<DaySummary> days)
        {
            var notes = new string[days.Count];
            for (int i = 0; i < days.Count; i++)
            {
                var top = days[i].Apps.Where(a => AppSeconds(a) >= 30).OrderByDescending(AppSeconds).Take(2).ToList();
                if (top.Count > 0)
                    notes[i] = "常用：" + string.Join(" · ", top.Select(a => (string.IsNullOrWhiteSpace(a.Name) ? "未识别应用" : a.Name) + " " + FormatDuration(AppSeconds(a))));
            }
            return notes;
        }

        private static DaySummary Combine(List<DaySummary> days)
        {
            var apps = days.SelectMany(d => d.Apps).GroupBy(a => a.AppId, StringComparer.OrdinalIgnoreCase).Select(g => new AppUsage { AppId = g.Key, Name = g.Last().Name, Seconds = g.Sum(a => a.Seconds), ForegroundSeconds = g.Sum(a => a.ForegroundSeconds), VisibleSeconds = g.Sum(a => a.VisibleSeconds), MediaSeconds = g.Sum(a => a.MediaSeconds), GamepadSeconds = g.Sum(a => a.GamepadSeconds) }).OrderByDescending(a => a.Seconds).ToList();
            double foreground = days.Sum(d => d.ForegroundSeconds);
            foreach (var app in apps) app.Share = foreground > 0 ? app.Seconds / foreground : 0;
            return new DaySummary
            {
                ScreenSeconds = days.Sum(d => d.ScreenSeconds), ForegroundSeconds = foreground, LockedSeconds = days.Sum(d => d.LockedSeconds),
                RawForegroundSeconds = days.Sum(d => d.RawForegroundSeconds), HasLegacyActivity = days.Any(d => d.HasLegacyActivity),
                VisibleSeconds = days.Sum(d => d.VisibleSeconds), VisibilityObservedSeconds = days.Sum(d => d.VisibilityObservedSeconds), VisibilityFirstRecorded = days.Min(d => d.VisibilityFirstRecorded),
                VisibleHourly = SumHours(days.Select(d => d.VisibleHourly)), VisibilityObservedHourly = SumHours(days.Select(d => d.VisibilityObservedHourly)), AppVisibleHourly = SumAppHours(days.Select(d => d.AppVisibleHourly)),
                ObservedSeconds = days.Sum(d => d.ObservedSeconds), UnknownSeconds = days.Sum(d => d.UnknownSeconds), Apps = apps,
                ForegroundHourly = SumHours(days.Select(d => d.ForegroundHourly)), RawForegroundHourly = SumHours(days.Select(d => d.RawForegroundHourly)),
                ScreenHourly = SumHours(days.Select(d => d.ScreenHourly)), ObservedHourly = SumHours(days.Select(d => d.ObservedHourly)), UnknownHourly = SumHours(days.Select(d => d.UnknownHourly)),
                AppHourly = SumAppHours(days.Select(d => d.AppHourly)), AppForegroundHourly = SumAppHours(days.Select(d => d.AppForegroundHourly)),
                HasData = days.Any(d => d.HasData), HasGaps = days.Any(d => d.HasGaps) || days.Count(d => d.HasData) < 7,
                HasHourlyDetails = days.All(d => d.HasHourlyDetails), FirstRecorded = days.Min(d => d.FirstRecorded), LastRecorded = days.Max(d => d.LastRecorded)
            };
        }

        private static double[] SumHours(IEnumerable<double[]> arrays) => Enumerable.Range(0, 24).Select(h => arrays.Sum(a => h < a.Length ? a[h] : 0)).ToArray();
        private static Dictionary<string, double[]> SumAppHours(IEnumerable<Dictionary<string, double[]>> maps) => maps.SelectMany(m => m)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => SumHours(g.Select(p => p.Value)), StringComparer.OrdinalIgnoreCase);
        private string MetricName => _showVisible ? "可见" : _showRawForeground ? "前台" : "活跃";
        private double MetricTotal(DaySummary day) => _showVisible ? day.VisibleSeconds : _showRawForeground ? day.RawForegroundSeconds : day.ForegroundSeconds;
        private double AppSeconds(AppUsage app) => _showVisible ? app.VisibleSeconds : _showRawForeground ? app.ForegroundSeconds : app.Seconds;
        private double AppShare(AppUsage app)
        {
            double total = _summary == null ? 0 : MetricTotal(_summary);
            return total > 0 ? AppSeconds(app) / total : 0;
        }
        public const string LegacyActivityExplanation = "包含旧版本记录：前台时长的“≥”表示已知下限，旧的空闲归属无法精确补算。";
        public const string VisibilityExplanation = "可见时间按窗口范围估算，排除最小化、完全遮挡、其他虚拟桌面及只露出很小部分的窗口。同应用多窗口合并；不同应用可同时累计，总和可超过亮屏。包含无操作时段，不代表专注；透明及特殊窗口可能有误差。";
        public static string FormatForegroundDuration(double seconds, bool lowerBound) => (lowerBound ? "≥ " : "") + FormatDuration(seconds);

        private void RenderTopApps(List<AppUsage> apps)
        {
            UpdateAppList(TopAppsHost, apps.Where(a => AppSeconds(a) > 0).OrderByDescending(AppSeconds).Take(6).ToList(), false, _showVisible ? "此范围暂无可见时间" : "应用使用记录会出现在这里", _showVisible ? "仅记录升级后采集到的窗口状态，历史无法补算。" : "开始记录后，试着切换几个软件。");
        }

        private void RenderAllApps(List<AppUsage> apps)
        {
            string search = SearchBox.Text?.Trim() ?? "";
            if (search != _lastAppSearch) { _appPage = 0; _lastAppSearch = search; }
            var filtered = apps.Where(a => AppSeconds(a) > 0 && a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(AppSeconds).ToList();
            int pages = Math.Max(1, (filtered.Count + AppsPerPage - 1) / AppsPerPage);
            _appPage = Math.Clamp(_appPage, 0, pages - 1);
            AppPages.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            AppPageText.Text = (_appPage + 1) + " / " + pages;
            PreviousAppPage.IsEnabled = _appPage > 0;
            NextAppPage.IsEnabled = _appPage + 1 < pages;
            AppDurationHeading.Text = MetricName + "时长";
            AppCountText.Text = (search.Length > 0 ? "匹配 " + filtered.Count + " / " : "") + "共 " + apps.Count + " 个应用与系统条目 · 按" + MetricName + "时长排序";
            AppBarCaptionText.Text = RangeText() + " · " + MetricName + "时长 · " + (filtered.Count == 0 ? (search.Length > 0 ? "没有匹配的应用" : "暂无应用记录") : "显示前 " + Math.Min(AppUsageBarChart.MaximumRows, filtered.Count) + " 个");
            AppsBarChart.SetApps(filtered, search.Length > 0 ? "没有匹配的应用，试试其他名称。" : "这段时间还没有应用使用记录。开启记录后切换几个软件即可查看。",
                _showRawForeground, _summary?.HasLegacyActivity == true, _summary == null ? 0 : MetricTotal(_summary), _showVisible, _controller.GetAppIcon);
            AppBarCoverageText.Text = "柱条从零起按时长比较，占比按所选范围全部应用计算，不含本软件。点击柱条查看详情。" +
                (_summary?.HasGaps == true ? " 仅含实际已记录区间，未记录时段不代表零使用。" : "") +
                (_showRawForeground && _summary?.HasLegacyActivity == true ? " 前台柱条为已知下限，占比也仅按已知时长计算。" : "");
            if (_showVisible) AppBarCoverageText.Text = VisibilityExplanation + " 占比按应用可见时间之和计算。已采集窗口状态 " + FormatDuration(_summary?.VisibilityObservedSeconds ?? 0) + "；旧记录无法补算。";
            UpdateAppList(AllAppsHost, filtered.Skip(_appPage * AppsPerPage).Take(AppsPerPage).ToList(), true, search.Length > 0 ? "没有找到匹配的应用" : "这段时间还没有应用记录", search.Length > 0 ? "试试其他名称。" : "开启记录后，前台软件会自动出现在这里。");
        }
        private void PreviousApps_Click(object sender, RoutedEventArgs e) { _appPage = Math.Max(0, _appPage - 1); Render(); }
        private void NextApps_Click(object sender, RoutedEventArgs e) { _appPage++; Render(); }

        private sealed class AppRowView
        {
            public Button Button = new Button();
            public TextBlock NameText = new TextBlock();
            public TextBlock DurationText = new TextBlock();
            public TextBlock? PercentText;
            public Border Icon = new Border();
            public Border? Track;
            public Border? Fill;
            public ImageSource? IconSource;
            public string FallbackName = "";
            public double Share;
        }

        private void UpdateAppList(StackPanel host, List<AppUsage> apps, bool full, string emptyTitle, string emptyCaption, bool compact = false)
        {
            if (!_rowCaches.TryGetValue(host, out var cache)) { cache = new Dictionary<string, AppRowView>(StringComparer.OrdinalIgnoreCase); _rowCaches[host] = cache; }
            if (apps.Count == 0)
            {
                string marker = "empty:" + emptyTitle;
                if (host.Children.Count != 1 || (host.Children[0] as FrameworkElement)?.Tag as string != marker)
                {
                    host.Children.Clear();
                    var empty = (FrameworkElement)EmptyMessage(emptyTitle, emptyCaption);
                    empty.Tag = marker;
                    if (compact) empty.Margin = new Thickness(8, 14, 8, 14);
                    host.Children.Add(empty);
                }
                cache.Clear();
                return;
            }
            IInputElement? focused = host.IsKeyboardFocusWithin ? Keyboard.FocusedElement : null;
            var desired = new List<Button>();
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double topSeconds = apps.Count > 0 ? Math.Max(1, AppSeconds(apps[0])) : 1;
            int rank = 0;
            foreach (var app in apps)
            {
                string id = app.AppId ?? "unknown-app";
                if (!activeIds.Add(id)) continue;
                if (!cache.TryGetValue(id, out var row)) { row = CreateAppRow(full, compact); cache[id] = row; }
                UpdateAppRow(row, app, topSeconds, rank++);
                desired.Add(row.Button);
            }
            // Keep the exact button instances when only their text/values changed.
            // Reorder existing instances only when the app order itself changes.
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < host.Children.Count && ReferenceEquals(host.Children[i], desired[i])) continue;
                if (host.Children.Contains(desired[i])) host.Children.Remove(desired[i]);
                host.Children.Insert(i, desired[i]);
            }
            while (host.Children.Count > desired.Count) host.Children.RemoveAt(host.Children.Count - 1);
            foreach (string id in cache.Keys.Where(id => !activeIds.Contains(id)).ToList()) cache.Remove(id);
            if (focused != null && focused is DependencyObject focusObject && IsAncestorOf(focusObject)) Keyboard.Focus(focused);
        }

        private AppRowView CreateAppRow(bool full, bool compact)
        {
            var row = new AppRowView();
            if (!full)
            {
                // Clean Bento row: 20px icon + App Name (left) + Duration (right) over a sleek 5px full-pill bar.
                var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
                var topLine = new Grid();
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Icon = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                row.NameText = new TextBlock { FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                row.DurationText = new TextBlock { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                row.DurationText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                Typography.SetNumeralAlignment(row.DurationText, FontNumeralAlignment.Tabular);
                Grid.SetColumn(row.NameText, 1);
                Grid.SetColumn(row.DurationText, 2);
                topLine.Children.Add(row.Icon);
                topLine.Children.Add(row.NameText);
                topLine.Children.Add(row.DurationText);
                stack.Children.Add(topLine);

                row.Track = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(26, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                row.Track.SetResourceReference(Border.BackgroundProperty, "ProgressTrack");
                row.Fill = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
                row.Track.SizeChanged += (_, _) => UpdateProgress(row);
                row.Track.Child = row.Fill;
                stack.Children.Add(row.Track);

                row.Button = new Button { Content = stack, Style = (Style)FindResource("RowButton"), Padding = new Thickness(6, 2, 6, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                row.Button.Click += (_, _) => { if (row.Button.Tag is AppUsage current) ShowAppDetails(current, compact ? _controller.Today : _selectedDate, !compact && _isWeek); };
                return row;
            }

            var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.Icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(row.Icon);
            row.NameText = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetColumn(row.NameText, 1); grid.Children.Add(row.NameText);
            row.DurationText = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Typography.SetNumeralAlignment(row.DurationText, FontNumeralAlignment.Tabular);
            Grid.SetColumn(row.DurationText, 2); grid.Children.Add(row.DurationText);
            row.PercentText = new TextBlock { FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            row.PercentText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Typography.SetNumeralAlignment(row.PercentText, FontNumeralAlignment.Tabular);
            Grid.SetColumn(row.PercentText, 3); grid.Children.Add(row.PercentText);
            row.Button = new Button { Content = grid, Style = (Style)FindResource("RowButton"), Padding = new Thickness(6, 2, 6, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            row.Button.Click += (_, _) => { if (row.Button.Tag is AppUsage current) ShowAppDetails(current, compact ? _controller.Today : _selectedDate, !compact && _isWeek); };
            return row;
        }

        private static void UpdateProgress(AppRowView row)
        {
            if (row.Fill != null && row.Track != null)
                row.Fill.Width = Math.Max(0, row.Track.ActualWidth * Math.Clamp(row.Share, 0, 1));
        }

        private void UpdateAppRow(AppRowView row, AppUsage app, double topSeconds, int rank)
        {
            row.Button.Tag = app;
            string name = string.IsNullOrWhiteSpace(app.Name) ? "未识别应用" : app.Name;
            string color = AppColors[(int)((uint)StableHash(app.AppId ?? name) % AppColors.Length)];
            row.NameText.Text = name;
            row.NameText.ToolTip = name;
            bool legacy = _summary?.HasLegacyActivity == true;
            double share = AppShare(app);
            row.Share = topSeconds > 0 ? Math.Clamp(AppSeconds(app) / topSeconds, 0, 1) : share;
            row.DurationText.Text = _showRawForeground ? FormatForegroundDuration(app.ForegroundSeconds, legacy) : FormatDuration(AppSeconds(app));
            System.Windows.Automation.AutomationProperties.SetName(row.Button, name + "，" + MetricName + "时长 " + row.DurationText.Text + "，打开应用详情");
            row.Button.ToolTip = name + "\n活跃 " + FormatDuration(app.Seconds) + "\n前台 " + FormatForegroundDuration(app.ForegroundSeconds, legacy) + "\n可见 " + FormatDuration(app.VisibleSeconds) + "\n点击查看详情" + (legacy ? "\n" + LegacyActivityExplanation : "");
            if (row.PercentText != null) row.PercentText.Text = share.ToString("P0", CultureInfo.CurrentCulture);
            if (row.Fill != null)
            {
                row.Fill.SetResourceReference(Border.BackgroundProperty, "ProgressBarBrush");
                row.Fill.Opacity = Math.Max(0.45, 1.0 - rank * 0.12);
            }
            UpdateProgress(row);
            if (row.Icon.Parent == null) return;
            ImageSource? applicationIcon = null;
            try { applicationIcon = _controller.GetAppIcon(app.AppId ?? "unknown-app"); } catch { }
            if (applicationIcon != null)
            {
                if (!ReferenceEquals(row.IconSource, applicationIcon))
                {
                    row.Icon.Background = Brushes.Transparent;
                    row.Icon.Child = new Image { Source = applicationIcon, Width = row.Icon.Width - 2, Height = row.Icon.Height - 2, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    row.IconSource = applicationIcon;
                }
            }
            else if (row.IconSource != null || row.FallbackName != name)
            {
                row.IconSource = null;
                row.FallbackName = name;
                row.Icon.Background = Brush(color);
                row.Icon.Child = new TextBlock { Text = name.Substring(0, Math.Min(name.Length, name[0] <= 127 ? 2 : 1)).ToUpperInvariant(), Foreground = Brushes.White, FontSize = row.Icon.Width <= 22 ? 9 : 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            }
        }

        private static int StableHash(string value)
        {
            unchecked { int result = 17; foreach (char c in value) result = result * 31 + c; return result; }
        }

        private UIElement EmptyMessage(string title, string caption)
        {
            var panel = new StackPanel { Margin = new Thickness(10, 24, 10, 24), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = "◷", FontSize = 28, Foreground = Brush("#C8CAE7"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 13) });
            panel.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = Brush("#818BA1"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = caption, FontSize = 10, Foreground = Brush("#A8AFBF"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            return panel;
        }

        private DateTime StatisticsNow()
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.Now, _controller.TimeZoneId).DateTime;
        }

        private void SetHourlyCoverage(UsageChart chart, DaySummary day, DateTime date)
        {
            DateTime now = StatisticsNow();
            bool[] future = Enumerable.Range(0, 24).Select(h => date.Date > now.Date || (date.Date == now.Date && h > now.Hour)).ToArray();
            double[] expected = Enumerable.Range(0, 24).Select(h => date.Date < now.Date ? 3600d : date.Date > now.Date ? 0d : Math.Clamp(now.TimeOfDay.TotalSeconds - h * 3600, 0, 3600)).ToArray();
            chart.SetCoverage(_showVisible ? day.VisibilityObservedHourly : day.ObservedHourly, _showVisible ? Array.Empty<double>() : day.UnknownHourly, future, expected,
                _showVisible && !chart.ForegroundOnly ? day.ObservedHourly : null);
        }

        private void SetWeekCoverage(UsageChart chart, List<DaySummary> days)
        {
            DateTime now = StatisticsNow();
            DateTime[] dates = days.Select(d => DateTime.TryParse(d.Date, out var date) ? date.Date : now.Date).ToArray();
            chart.SetCoverage(days.Select(d => _showVisible ? d.VisibilityObservedSeconds : d.ObservedSeconds).ToArray(), days.Select(d => _showVisible ? 0 : d.UnknownSeconds).ToArray(), dates.Select(d => d > now.Date).ToArray(),
                dates.Select(d => d < now.Date ? 86400d : d > now.Date ? 0d : now.TimeOfDay.TotalSeconds).ToArray(),
                _showVisible && !chart.ForegroundOnly ? days.Select(d => d.ObservedSeconds).ToArray() : null);
        }

        private void ShowAppDetails(AppUsage app, DateTime selectedDate, bool isWeek)
        {
            Safe(() =>
            {
                var days = _controller.GetWeek(selectedDate);
                var day = _controller.GetDay(selectedDate);
                var selected = isWeek ? Combine(days) : day;
                var selectedApp = selected.Apps.FirstOrDefault(a => string.Equals(a.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)) ?? app;

                var dialog = new Window
                {
                    Owner = this,
                    Title = app.Name + " · 使用详情",
                    Width = 680,
                    Height = 690,
                    MinWidth = 540,
                    MinHeight = 460,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.CanResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowInTaskbar = false,
                    UseLayoutRounding = true,
                    FontFamily = FontFamily,
                    FontSize = 13
                };
                TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
                var chrome = new WindowChrome
                {
                    CaptionHeight = 40,
                    ResizeBorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(10),
                    GlassFrameThickness = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? new Thickness(1) : new Thickness(0),
                    UseAeroCaptionButtons = false
                };
                WindowChrome.SetWindowChrome(dialog, chrome);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    dialog.SourceInitialized += (_, _) =>
                    {
                        int pref = 2;
                        DwmSetWindowAttribute(new WindowInteropHelper(dialog).Handle, 33, ref pref, sizeof(int));
                    };
                }
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) });
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Caption.xaml", UriKind.Relative) });
                ThemeManager.Apply(dialog, _controller.DarkMode);

                var root = new Grid();
                root.SetResourceReference(Grid.BackgroundProperty, "GlassBackdrop");
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var edgeDepth = new Border { IsHitTestVisible = false };
                edgeDepth.SetResourceReference(Border.BackgroundProperty, "MainWindowEdgeDepth");
                Grid.SetRowSpan(edgeDepth, 2);
                root.Children.Add(edgeDepth);

                // Custom glass title bar
                var captionBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
                captionBorder.SetResourceReference(Border.BorderBrushProperty, "GlassInset");
                var captionGrid = new Grid();
                captionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                captionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var captionTitle = new TextBlock { Text = app.Name + " · 使用详情", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
                captionTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                captionGrid.Children.Add(captionTitle);
                var closePath = new Path { Width = 10, Height = 10, Data = Geometry.Parse("M0.5,0.5 L9.5,9.5 M9.5,0.5 L0.5,9.5"), StrokeThickness = 1 };
                closePath.SetResourceReference(Path.StrokeProperty, "Ink");
                var closeBtn = new Button { Content = closePath, Height = 40, Style = (Style)dialog.FindResource("CaptionCloseButton"), ToolTip = "关闭" };
                WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
                closeBtn.Click += (_, _) => dialog.Close();
                Grid.SetColumn(closeBtn, 1);
                captionGrid.Children.Add(closeBtn);
                captionBorder.Child = captionGrid;
                root.Children.Add(captionBorder);

                var panel = new StackPanel { Margin = new Thickness(20, 14, 20, 20) };

                // Hero header card with icon + name + 3 KPI cards
                var heroCard = new Border { Style = (Style)dialog.FindResource("Card"), CornerRadius = new CornerRadius(20), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 12) };
                heroCard.SetResourceReference(Border.BackgroundProperty, "GlassHero");
                heroCard.SetResourceReference(Border.BorderBrushProperty, "GlassEdge");
                var heroStack = new StackPanel();
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                ImageSource? appIcon = null;
                try { appIcon = _controller.GetAppIcon(selectedApp.AppId ?? "unknown-app"); } catch { }
                if (appIcon != null)
                {
                    iconBox.Child = new Image { Source = appIcon, Width = 30, Height = 30, Stretch = Stretch.Uniform };
                }
                else
                {
                    string color = AppColors[(int)((uint)StableHash(selectedApp.AppId ?? app.Name) % AppColors.Length)];
                    iconBox.Background = Brush(color);
                    iconBox.Child = new TextBlock { Text = app.Name.Substring(0, Math.Min(app.Name.Length, app.Name[0] <= 127 ? 2 : 1)).ToUpperInvariant(), Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                headerGrid.Children.Add(iconBox);

                var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                titleStack.Children.Add(new TextBlock { Text = app.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                string range = isWeek ? selectedDate.AddDays(-6).ToString("yyyy/M/d") + " — " + selectedDate.ToString("M/d") : selectedDate.ToString("yyyy/M/d");
                var rangeBlock = new TextBlock { Text = range, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
                rangeBlock.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                titleStack.Children.Add(rangeBlock);
                Grid.SetColumn(titleStack, 1);
                headerGrid.Children.Add(titleStack);
                heroStack.Children.Add(headerGrid);

                // 3 side-by-side KPI mini cards
                var kpiGrid = new Grid();
                kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Border KpiCard(string label, string value, string caption, bool accent)
                {
                    var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(13, 10, 13, 10), BorderThickness = new Thickness(1) };
                    card.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSoft" : "GlassControl");
                    card.SetResourceReference(Border.BorderBrushProperty, accent ? "GlassEdge" : "GlassCardEdge");
                    var st = new StackPanel();
                    var lb = new TextBlock { Text = label, FontSize = 11.5 };
                    lb.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    var val = new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 2) };
                    val.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentText" : "Ink");
                    Typography.SetNumeralAlignment(val, FontNumeralAlignment.Tabular);
                    var cap = new TextBlock { Text = caption, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
                    cap.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
                    st.Children.Add(lb); st.Children.Add(val); st.Children.Add(cap);
                    card.Child = st;
                    return card;
                }

                var activeCard = KpiCard("活跃时长", FormatDuration(selectedApp.Seconds), "已扣除空闲", true);
                var fgCard = KpiCard("前台时长", FormatForegroundDuration(selectedApp.ForegroundSeconds, selected.HasLegacyActivity), "包含空闲时段", false);
                var visCard = KpiCard("可见时长", selected.VisibilityObservedSeconds > 0 ? FormatDuration(selectedApp.VisibleSeconds) : "—", selected.VisibilityObservedSeconds > 0 ? "多窗口并行估算" : "尚未采集", false);
                Grid.SetColumn(activeCard, 0); Grid.SetColumn(fgCard, 2); Grid.SetColumn(visCard, 4);
                kpiGrid.Children.Add(activeCard); kpiGrid.Children.Add(fgCard); kpiGrid.Children.Add(visCard);
                heroStack.Children.Add(kpiGrid);

                var evidenceText = new TextBlock
                {
                    Text = "补充计时：媒体推测 " + FormatDuration(selectedApp.MediaSeconds) + " · 手柄输入 " + FormatDuration(selectedApp.GamepadSeconds) + "（已计入活跃，不重复累加）",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                evidenceText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                heroStack.Children.Add(evidenceText);
                if (selected.HasLegacyActivity)
                {
                    var leg = new TextBlock { Text = LegacyActivityExplanation, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                    leg.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
                    heroStack.Children.Add(leg);
                }
                heroCard.Child = heroStack;
                panel.Children.Add(heroCard);

                // Idle rule card
                var ruleCard = new Border { Style = (Style)dialog.FindResource("Card"), Padding = new Thickness(16, 13, 16, 13), Margin = new Thickness(0, 0, 0, 12) };
                var ruleGrid = new Grid();
                ruleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ruleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var ruleInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                ruleInfo.Children.Add(new TextBlock { Text = "此应用的空闲规则", FontSize = 13.5, FontWeight = FontWeights.SemiBold });
                var ruleHint = new TextBlock { Text = "仅在启用空闲过滤时生效，只影响之后的记录；始终计时不含后台与锁屏。", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                ruleHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                ruleInfo.Children.Add(ruleHint);
                ruleGrid.Children.Add(ruleInfo);

                var idleSelector = new ComboBox { Width = 205, Height = 32, VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("IdleRuleSelector") };
                int? selectedMinutes = _controller.GetAppIdleTimeoutMinutes(app.AppId);
                var options = new[] { ("global", "跟随全局（" + _controller.IdleTimeoutMinutes + " 分钟）"), ("1", "1 分钟"), ("3", "3 分钟"), ("5", "5 分钟"), ("10", "10 分钟"), ("15", "15 分钟"), ("30", "30 分钟"), ("0", "在前台时始终计时") };
                string selectedTag = selectedMinutes?.ToString(CultureInfo.InvariantCulture) ?? "global";
                foreach (var option in options)
                {
                    var item = new ComboBoxItem { Content = option.Item2, Tag = option.Item1 };
                    idleSelector.Items.Add(item);
                    if (option.Item1 == selectedTag) idleSelector.SelectedItem = item;
                }
                System.Windows.Automation.AutomationProperties.SetName(idleSelector, "此应用的空闲规则");
                idleSelector.SelectionChanged += (_, _) => Safe(() =>
                {
                    string tag = (idleSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "global";
                    _controller.SetAppIdleTimeoutMinutes(app.AppId, tag == "global" ? null : int.Parse(tag, CultureInfo.InvariantCulture));
                });
                Grid.SetColumn(idleSelector, 1);
                ruleGrid.Children.Add(idleSelector);
                ruleCard.Child = ruleGrid;
                panel.Children.Add(ruleCard);

                // Day hourly chart card
                var dayCard = new Border { Style = (Style)dialog.FindResource("Card"), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12) };
                var dayStack = new StackPanel();
                dayStack.Children.Add(new TextBlock { Text = selectedDate.ToString("M 月 d 日") + " · " + MetricName + "时长 · 按小时查看", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
                if (day.HasHourlyDetails)
                {
                    var hours = _showVisible ? day.AppVisibleHourly : _showRawForeground ? day.AppForegroundHourly : day.AppHourly;
                    double[] hourly = hours.TryGetValue(app.AppId, out var appHours) ? appHours : new double[24];
                    var dayChart = new UsageChart { Height = 138, ForegroundOnly = true, MetricLabel = MetricName, MetricIsLowerBound = _showRawForeground && day.HasLegacyActivity };
                    dayChart.SetData(Array.Empty<double>(), hourly, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(), _showVisible ? day.VisibilityObservedSeconds > 0 : day.HasData, "该日期未记录此指标，不能视为零使用");
                    SetHourlyCoverage(dayChart, day, selectedDate);
                    AddChartWithDetails(dayStack, dayChart, app.Name + " · " + selectedDate.ToString("M 月 d 日") + "分时明细");
                }
                else
                {
                    var expired = new TextBlock { Text = "该日期的小时明细已过保留期，仍可查看每日汇总。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
                    expired.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    dayStack.Children.Add(expired);
                }
                dayCard.Child = dayStack;
                panel.Children.Add(dayCard);

                // 7-day trend chart card
                var weekCard = new Border { Style = (Style)dialog.FindResource("Card"), Padding = new Thickness(16, 14, 16, 14) };
                var weekStack = new StackPanel();
                weekStack.Children.Add(new TextBlock { Text = "最近七天 · " + MetricName + "时长", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
                var chart = new UsageChart { Height = 126, ForegroundOnly = true, MetricLabel = MetricName, MetricIsLowerBound = _showRawForeground && days.Any(d => d.HasLegacyActivity) };
                double[] values = days.Select(d => d.Apps.Where(a => string.Equals(a.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)).Sum(AppSeconds)).ToArray();
                chart.SetData(Array.Empty<double>(), values, days.Select(d => DateTime.TryParse(d.Date, out var dt) ? dt.ToString("M/d") : d.Date).ToArray(), days.Any(d => _showVisible ? d.VisibilityObservedSeconds > 0 : d.HasData));
                SetWeekCoverage(chart, days);
                AddChartWithDetails(weekStack, chart, app.Name + " · 近 7 天明细");
                var footnote = new TextBlock { Text = "前台时长包含空闲；活跃时长按记录时的空闲规则估算。灰纹为未记录/部分记录，浅底为未来时间。", FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
                footnote.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
                weekStack.Children.Add(footnote);
                weekCard.Child = weekStack;
                panel.Children.Add(weekCard);

                var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                scroller.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), new Style(typeof(System.Windows.Controls.Primitives.ScrollBar), (Style)dialog.FindResource("GlassScrollBar")));
                Grid.SetRow(scroller, 1);
                root.Children.Add(scroller);
                dialog.Content = root;
                dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); } };
                dialog.ShowDialog();
            });
        }

        private void AddChartWithDetails(Panel panel, UsageChart chart, string title)
        {
            panel.Children.Add(chart);
            var details = new Button
            {
                Content = "数据明细",
                Style = (Style)FindResource("RoundButton"),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 5, 0, 0)
            };
            System.Windows.Automation.AutomationProperties.SetName(details, "查看" + title);
            details.Click += (_, _) => Safe(() => chart.ShowDetails(title));
            panel.Children.Add(details);
        }

        public static string FormatDuration(double seconds)
        {
            seconds = Math.Max(0, seconds);
            if (seconds < 60) return ((int)Math.Floor(seconds)).ToString(CultureInfo.InvariantCulture) + "秒";
            int totalMinutes = (int)Math.Floor(seconds / 60);
            int remSeconds = (int)Math.Floor(seconds) % 60;
            if (totalMinutes < 60)
                return remSeconds == 0 ? totalMinutes + "分" : totalMinutes + "分" + remSeconds.ToString("00", CultureInfo.InvariantCulture) + "秒";
            int hours = totalMinutes / 60;
            int remMinutes = totalMinutes % 60;
            return remMinutes == 0 ? hours + "小时" : hours + "小时" + remMinutes.ToString("00", CultureInfo.InvariantCulture) + "分";
        }

        private string RangeText() => _isWeek ? _selectedDate.AddDays(-6).ToString("yyyy/M/d") + " — " + _selectedDate.ToString("M/d") : _selectedDate.ToString("yyyy/M/d");
        private static Brush Brush(string color) => ThemeManager.LegacyBrush(color);
        private void ChartDetails_Click(object sender, RoutedEventArgs e) =>
            Safe(() => MainChart.ShowDetails((_isWeek ? "近 7 天" : _selectedDate.ToString("M 月 d 日")) + " · 亮屏与" + MetricName + "明细"));

        private bool AcceptSelection => _ready && !_rendering;

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!AcceptSelection) return;
            _page = (sender as RadioButton)?.Tag as string ?? "overview";
            Render();
        }

        private void AllApps_Click(object sender, RoutedEventArgs e) { _page = "apps"; Render(); }
        private void PrevDate_Click(object sender, RoutedEventArgs e) => SelectDate(_selectedDate.AddDays(-1), false);
        private void NextDate_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDate < _controller.Today) SelectDate(_selectedDate.AddDays(1), false);
        }
        private void Today_Checked(object sender, RoutedEventArgs e) { if (AcceptSelection) SelectDate(_controller.Today, false); }
        private void Yesterday_Checked(object sender, RoutedEventArgs e) { if (AcceptSelection) SelectDate(_controller.Today.AddDays(-1), false); }
        private void Week_Checked(object sender, RoutedEventArgs e) { if (AcceptSelection) SelectDate(_controller.Today, true); }
        private void ActiveMetric_Checked(object sender, RoutedEventArgs e) => SelectMetric(showVisible: false, showFront: false);
        private void ForegroundMetric_Checked(object sender, RoutedEventArgs e) => SelectMetric(showVisible: false, showFront: true);
        private void VisibleMetric_Checked(object sender, RoutedEventArgs e) => SelectMetric(showVisible: true, showFront: false);

        private void SelectMetric(bool showVisible, bool showFront)
        {
            if (!AcceptSelection) return;
            _showVisible = showVisible;
            _showRawForeground = showFront;
            _appPage = 0;
            Render();
        }
        private void Background_Click(object sender, RoutedEventArgs e) => Safe(() => BackgroundRequested?.Invoke());
        private void VisibilityDetection_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.VisibilityDetectionEnabled = VisibilityDetectionCheck.IsChecked == true); }
        private void SelectDate(DateTime date, bool week) { _selectedDate = date.Date; _isWeek = week; _followToday = week || _selectedDate == _controller.Today; _ready = false; DateSelector.SelectedDate = _selectedDate; _ready = true; Render(); }
        private void Date_Changed(object? sender, SelectionChangedEventArgs e) { if (_ready && !_rendering && DateSelector.SelectedDate.HasValue) { _selectedDate = DateSelector.SelectedDate.Value.Date; _isWeek = false; _followToday = _selectedDate == _controller.Today; Render(); } }
        private void Collapse_Click(object sender, RoutedEventArgs e) => Safe(() => WidgetRequested?.Invoke());
        private void Pin_Click(object sender, RoutedEventArgs e) => Safe(() => { _controller.AlwaysOnTop = !_controller.AlwaysOnTop; Render(); });
        private void DockMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void DockMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void DockClose_Click(object sender, RoutedEventArgs e) => Close();
        private void WidgetMode_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.WidgetEnabled = WidgetModeCheck.IsChecked == true); }
        private void AutoRecord_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AutoRecord = AutoRecordCheck.IsChecked == true); }
        private void AutoStartWidget_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AutoStartWidget = AutoStartWidgetCheck.IsChecked == true); }
        private void IdleFilter_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.IdleFilterEnabled = IdleFilterCheck.IsChecked == true); }
        private void MediaDetection_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.MediaDetectionEnabled = MediaDetectionCheck.IsChecked == true); }
        private void GamepadDetection_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.GamepadDetectionEnabled = GamepadDetectionCheck.IsChecked == true); }
        private void IdleTimeout_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && !_rendering && int.TryParse(IdleTimeoutSelector.SelectedValue as string, out int minutes))
                Safe(() => _controller.IdleTimeoutMinutes = minutes);
        }
        private void Theme_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _rendering) return;
            Safe(() =>
            {
                string? mode = ThemeSelector.SelectedValue as string;
                if (mode == "system") _controller.FollowSystemTheme = true;
                else if (mode is "light" or "dark") _controller.DarkMode = mode == "dark";
            });
        }
        private void Topmost_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AlwaysOnTop = TopmostCheck.IsChecked == true); }
        private void Exit_Click(object sender, RoutedEventArgs e) => _controller.Exit();
        private void Refresh_Click(object sender, RoutedEventArgs e) => Safe(() => { InvalidateWeekCache(); _controller.Refresh(); Render(); });
        private void Start_Click(object sender, RoutedEventArgs e) => StartRecording();
        private void StartRecording() => Safe(() =>
        {
            _controller.StartRecording();
            try { _controller.AutoStart = WelcomeAutoStart.IsChecked == true; }
            catch (Exception ex) { MessageBox.Show(this, "已经开始记录，但自启动设置未完成：\n" + ex.Message + "\n\n你可以稍后在设置中重试。", "自启动设置", MessageBoxButton.OK, MessageBoxImage.Information); }
            Render();
        });
        private void StatusPill_Click(object sender, MouseButtonEventArgs e) => Pause_Click(sender, new RoutedEventArgs());
        private void Pause_Click(object sender, RoutedEventArgs e) { if (!_controller.IsRecordingEnabled) StartRecording(); else Safe(() => { _controller.TogglePause(); Render(); }); }
        private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready && _summary != null) RenderAllApps(_summary.Apps); }
        private void AutoStart_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => { _controller.AutoStart = AutoStartCheck.IsChecked == true; }); }
        private void OpenData_Click(object sender, RoutedEventArgs e) => Safe(() => _controller.OpenDataDirectory());

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Title = "导出使用记录", InitialDirectory = System.IO.Path.GetDirectoryName(_controller.DataDirectory) ?? _controller.DataDirectory, FileName = "屏幕时间-" + _selectedDate.ToString("yyyy-MM-dd") + (_isWeek ? "-近7天" : "") + ".csv", Filter = "CSV 表格 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true };
            if (dialog.ShowDialog(this) == true) Safe(() => { _controller.ExportCsv(dialog.FileName, _isWeek ? _selectedDate.AddDays(-6) : _selectedDate, _selectedDate); MessageBox.Show(this, "记录已导出。CSV 是汇总表格，不包含窗口标题或网页内容。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information); });
        }

        private void DeleteDate_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定删除 " + RangeText() + " 已有的使用记录？\n\n明细和汇总会一起删除，无法撤销。删除不会停止之后的记录。", "删除选定记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                Safe(() => { InvalidateWeekCache(); _controller.DeleteRange(_isWeek ? _selectedDate.AddDays(-6) : _selectedDate, _selectedDate); Render(); });
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定删除所有已有的使用记录？\n\n明细、每日汇总及本程序管理的备份都会清除，无法撤销。已导出的文件不会删除。\n\n之后是否继续记录取决于当前记录开关。", "删除全部记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                Safe(() => { InvalidateWeekCache(); _controller.DeleteAll(); Render(); });
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
