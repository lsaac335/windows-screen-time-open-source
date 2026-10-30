using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private bool _ready;
        private bool _rendering;
        private bool _allowClose;
        private bool? _lastDarkMode;
        private string _page = "overview";
        private DaySummary? _summary;
        private List<DaySummary> _week = new List<DaySummary>();
        private readonly Dictionary<StackPanel, Dictionary<string, AppRowView>> _rowCaches = new Dictionary<StackPanel, Dictionary<string, AppRowView>>();
        private static readonly string[] AppColors = { "#6561E7", "#38A895", "#E5A252", "#528DCE", "#C779AE", "#7686AD" };
        public event Action? WidgetRequested;

        public MainWindow(IAppController controller)
        {
            _controller = controller;
            _selectedDate = _lastToday = controller.Today;
            InitializeComponent();
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(1160, workArea.Width);
            Height = Math.Min(830, workArea.Height);
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

        public void ShowAndActivate()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Render();
        }

        public void PrepareForExit() => _allowClose = true;

        public void ShowDashboard()
        {
            _page = "overview";
            SelectDate(_controller.Today, false);
            ShowAndActivate();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
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
                PauseButton.Content = !enabled ? "开始记录" : paused ? "▶  恢复记录" : "Ⅱ  暂停记录";
                CurrentAppText.Text = !enabled ? "从现在开始" : paused ? "记录已暂停" : string.IsNullOrWhiteSpace(_controller.CurrentAppName) ? "等待前台应用" : _controller.CurrentAppName;
                DisplayStateText.Text = _controller.DisplayStateText;
                WelcomePanel.Visibility = !enabled && _page != "settings" ? Visibility.Visible : Visibility.Collapsed;
                ErrorBanner.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
                ErrorText.Text = warning ? runtimeStatus + "。当前受影响区间不会推算为正常使用时间。" : "";
                AutoStartCheck.IsChecked = _controller.AutoStart;
                AutoRecordCheck.IsChecked = _controller.AutoRecord;
                AutoStartWidgetCheck.IsChecked = _controller.AutoStartWidget;
                IdleFilterCheck.IsChecked = _controller.IdleFilterEnabled;
                IdleTimeoutSelector.SelectedValue = _controller.IdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
                IdleTimeoutSelector.IsEnabled = _controller.IdleFilterEnabled;
                IdleRuleDescription.Text = "开启后，连续 " + _controller.IdleTimeoutMinutes + " 分钟无键盘或鼠标操作的后续区间不计入活跃时长；前台时长与亮屏时长仍继续累计。前 " + _controller.IdleTimeoutMinutes + " 分钟仍计入活跃。\n\n阅读、视频或手柄操作可能被判为空闲，可调整阈值、关闭过滤，或在应用详情里单独设置。修改只影响之后的记录，不重算历史。";
                ForegroundRuleText.Text = _controller.IdleFilterEnabled
                    ? "活跃时长按操作估算：全局阈值为 " + _controller.IdleTimeoutMinutes + " 分钟；应用可单独设置。前台时长包含空闲，阅读和视频可能被判为空闲。"
                    : "空闲过滤已关闭：之后记录的活跃时长与前台时长相同，仍只统计获得焦点的应用。";
                DarkModeCheck.IsChecked = _controller.DarkMode;
                TopmostCheck.IsChecked = _controller.AlwaysOnTop;
                WidgetModeCheck.IsChecked = _controller.WidgetEnabled;
                DataPathText.Text = _controller.DataDirectory;
                TimeZoneText.Text = "统计时区：" + _controller.TimeZoneId + "（固定，不随系统校时重排历史）";
                SelectedRangeText.Text = "当前选定范围：" + RangeText() + "。删除历史不会停止之后的记录。";
                PageTitle.Text = _page == "settings" ? "设置" : _page == "apps" ? "应用统计" : "使用概览";
                PageSubtitle.Text = _page == "settings" ? "你的记录，由你掌控。" : (_isWeek ? "最近七天，看看时间的流向。" : _selectedDate == today ? "让每一段时间，都看得见。" : _selectedDate.ToString("yyyy 年 M 月 d 日", CultureInfo.InvariantCulture) + " 的使用记录");
                SetNav(OverviewNav, _page == "overview");
                SetNav(AppsNav, _page == "apps");
                SetNav(SettingsNav, _page == "settings");
                OverviewPanel.Visibility = _page == "overview" ? Visibility.Visible : Visibility.Collapsed;
                AppsPanel.Visibility = _page == "apps" ? Visibility.Visible : Visibility.Collapsed;
                SettingsPanel.Visibility = _page == "settings" ? Visibility.Visible : Visibility.Collapsed;
                DateToolbar.Visibility = _page == "settings" ? Visibility.Collapsed : Visibility.Visible;
                MetricToolbar.Visibility = _page == "settings" ? Visibility.Collapsed : Visibility.Visible;
                SetDateButton(ActiveMetricButton, !_showRawForeground);
                SetDateButton(ForegroundMetricButton, _showRawForeground);
                SetDateButton(TodayButton, !_isWeek && _selectedDate == today);
                SetDateButton(YesterdayButton, !_isWeek && _selectedDate == today.AddDays(-1));
                SetDateButton(WeekButton, _isWeek);
                if (_page == "settings") return;

                if (_isWeek)
                {
                    _week = _controller.GetWeek(_selectedDate);
                    _summary = Combine(_week);
                }
                else _summary = _controller.GetDay(_selectedDate);
                var summary = _summary;
                ScreenValue.Text = summary.HasData ? FormatDuration(summary.ScreenSeconds) : "—";
                ForegroundValue.Text = summary.HasData ? FormatDuration(summary.ForegroundSeconds) : "—";
                RawForegroundValue.Text = summary.HasData ? FormatForegroundDuration(summary.RawForegroundSeconds, summary.HasLegacyActivity) : "—";
                RawForegroundCaption.Text = summary.HasLegacyActivity ? "≥ 为旧记录可确认的下限" : "包括空闲 · 不含本软件";
                RawForegroundValue.ToolTip = summary.HasLegacyActivity ? LegacyActivityExplanation : "应用获得前台焦点的累计时间，包括空闲，不含本软件。";
                LockedValue.Text = summary.HasData ? "其中锁屏亮屏 " + FormatDuration(summary.LockedSeconds) : "屏幕亮着的时间";
                MetricHintText.Text = _showRawForeground ? "包括空闲；只计获得焦点的应用" : "扣除超出阈值的空闲，是活跃估计值";
                ChartMetricLegend.Text = MetricName;
                MainChart.MetricLabel = MetricName;
                MainChart.MetricIsLowerBound = _showRawForeground && summary.HasLegacyActivity;
                if (_isWeek)
                {
                    ChartTitle.Text = "最近七天的使用分布";
                    ChartCaption.Text = RangeText() + " · 亮屏与" + MetricName + "时长";
                    MainChart.SetData(_week.Select(d => d.ScreenSeconds).ToArray(), _week.Select(d => _showRawForeground ? d.RawForegroundSeconds : d.ForegroundSeconds).ToArray(),
                        _week.Select(d => DateTime.TryParse(d.Date, out var date) ? date.ToString("M/d") : d.Date).ToArray(), summary.HasData);
                    SetWeekCoverage(MainChart, _week);
                    int valid = _week.Count(d => d.HasData);
                    CoverageText.Text = valid == 0 ? "这段时间还没有可用记录，灰纹表示未记录。" : "已记录 " + valid + "/7 天。灰纹为未记录或部分记录的日期，不能视为完整全天数据。";
                }
                else
                {
                    ChartTitle.Text = "一天中的使用分布";
                    ChartCaption.Text = _selectedDate.ToString("M 月 d 日") + " · 亮屏与" + MetricName + "时长";
                    bool detail = summary.HasHourlyDetails;
                    MainChart.SetData(summary.ScreenHourly, _showRawForeground ? summary.RawForegroundHourly : summary.ForegroundHourly, Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToArray(), summary.HasData && detail,
                        summary.HasData && !detail ? "小时明细已过保留期，仍可查看每日汇总" : "还没有记录，开始后时间会在这里呈现");
                    if (detail) SetHourlyCoverage(MainChart, summary, _selectedDate);
                    else MainChart.SetCoverage(Array.Empty<double>(), Array.Empty<double>());
                    CoverageText.Text = !summary.HasData ? "无记录 · 首次启用前、暂停与未运行的区间不会补记为零使用。" :
                        (summary.FirstRecorded.HasValue ? "当日自 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(summary.FirstRecorded.Value, _controller.TimeZoneId).ToString("HH:mm") + " 开始记录" : "已记录当前日期的一部分") +
                        (summary.HasGaps ? " · 数据不完整，灰纹为未记录/部分记录，浅底为未来时间。" : " · 仅汇总实际观测到的区间。");
                }
                if (summary.HasLegacyActivity) CoverageText.Text += " " + LegacyActivityExplanation;
                RenderTopApps(summary.Apps);
                RenderAllApps(summary.Apps);
            }
            catch (Exception ex)
            {
                ErrorBanner.Visibility = Visibility.Visible;
                ErrorText.Text = "读取记录时遇到问题：" + ex.Message;
            }
            finally { _rendering = false; }
        }

        private static DaySummary Combine(List<DaySummary> days)
        {
            var apps = days.SelectMany(d => d.Apps).GroupBy(a => a.AppId, StringComparer.OrdinalIgnoreCase).Select(g => new AppUsage { AppId = g.Key, Name = g.Last().Name, Seconds = g.Sum(a => a.Seconds), ForegroundSeconds = g.Sum(a => a.ForegroundSeconds) }).OrderByDescending(a => a.Seconds).ToList();
            double foreground = days.Sum(d => d.ForegroundSeconds);
            foreach (var app in apps) app.Share = foreground > 0 ? app.Seconds / foreground : 0;
            return new DaySummary
            {
                ScreenSeconds = days.Sum(d => d.ScreenSeconds), ForegroundSeconds = foreground, LockedSeconds = days.Sum(d => d.LockedSeconds),
                RawForegroundSeconds = days.Sum(d => d.RawForegroundSeconds), HasLegacyActivity = days.Any(d => d.HasLegacyActivity),
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
        private string MetricName => _showRawForeground ? "前台" : "活跃";
        private double AppSeconds(AppUsage app) => _showRawForeground ? app.ForegroundSeconds : app.Seconds;
        private double AppShare(AppUsage app)
        {
            double total = _showRawForeground ? _summary?.RawForegroundSeconds ?? 0 : _summary?.ForegroundSeconds ?? 0;
            return total > 0 ? AppSeconds(app) / total : 0;
        }
        public const string LegacyActivityExplanation = "包含旧版本记录：前台时长的“≥”表示已知下限，旧的空闲归属无法精确补算。";
        public static string FormatForegroundDuration(double seconds, bool lowerBound) => (lowerBound ? "≥ " : "") + FormatDuration(seconds);

        private void RenderTopApps(List<AppUsage> apps)
        {
            UpdateAppList(TopAppsHost, apps.OrderByDescending(AppSeconds).Take(5).ToList(), false, "应用使用记录会出现在这里", "开始记录后，试着切换几个软件。");
        }

        private void RenderAllApps(List<AppUsage> apps)
        {
            string search = SearchBox.Text?.Trim() ?? "";
            var filtered = apps.Where(a => a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(AppSeconds).ToList();
            AppDurationHeading.Text = MetricName + "时长";
            AppCountText.Text = (search.Length > 0 ? "匹配 " + filtered.Count + " / " : "") + "共 " + apps.Count + " 个应用与系统条目 · 按" + MetricName + "时长排序";
            AppBarCaptionText.Text = RangeText() + " · " + MetricName + "时长 · " + (filtered.Count == 0 ? (search.Length > 0 ? "没有匹配的应用" : "暂无应用记录") : "显示前 " + Math.Min(AppUsageBarChart.MaximumRows, filtered.Count) + " 个");
            AppsBarChart.SetApps(filtered, search.Length > 0 ? "没有匹配的应用，试试其他名称。" : "这段时间还没有应用使用记录。开启记录后切换几个软件即可查看。",
                _showRawForeground, _summary?.HasLegacyActivity == true, _showRawForeground ? _summary?.RawForegroundSeconds ?? 0 : _summary?.ForegroundSeconds ?? 0);
            AppBarCoverageText.Text = "柱条从零起按时长比较，占比按所选范围全部应用计算，不含本软件。点击柱条查看详情。" +
                (_summary?.HasGaps == true ? " 仅含实际已记录区间，未记录时段不代表零使用。" : "") +
                (_showRawForeground && _summary?.HasLegacyActivity == true ? " 前台柱条为已知下限，占比也仅按已知时长计算。" : "");
            UpdateAppList(AllAppsHost, filtered, true, search.Length > 0 ? "没有找到匹配的应用" : "这段时间还没有应用记录", search.Length > 0 ? "试试其他名称。" : "开启记录后，前台软件会自动出现在这里。");
        }

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
            foreach (var app in apps)
            {
                string id = app.AppId ?? "unknown-app";
                if (!activeIds.Add(id)) continue;
                if (!cache.TryGetValue(id, out var row)) { row = CreateAppRow(full, compact); cache[id] = row; }
                UpdateAppRow(row, app);
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
            var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(full ? 110 : compact ? 98 : 95) });
            if (full) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.Icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Left };
            grid.Children.Add(row.Icon);
            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            row.NameText = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            namePanel.Children.Add(row.NameText);
            if (!full)
            {
                row.Track = new Border { Height = 3, Background = Brush("#F0F1F7"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 7, 0, 0), MaxWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
                row.Fill = new Border { Height = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                row.Track.SizeChanged += (_, _) => UpdateProgress(row);
                row.Track.Child = row.Fill;
                namePanel.Children.Add(row.Track);
            }
            Grid.SetColumn(namePanel, 1); grid.Children.Add(namePanel);
            row.DurationText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(row.DurationText, 2); grid.Children.Add(row.DurationText);
            if (full)
            {
                row.PercentText = new TextBlock { Foreground = Brush("#8E97AA"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(row.PercentText, 3); grid.Children.Add(row.PercentText);
            }
            row.Button = new Button { Content = grid, Style = (Style)FindResource("RoundButton"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            row.Button.Click += (_, _) => { if (row.Button.Tag is AppUsage current) ShowAppDetails(current, compact ? _controller.Today : _selectedDate, !compact && _isWeek); };
            return row;
        }

        private static void UpdateProgress(AppRowView row)
        {
            if (row.Fill != null && row.Track != null)
                row.Fill.Width = Math.Max(0, row.Track.ActualWidth * Math.Clamp(row.Share, 0, 1));
        }

        private void UpdateAppRow(AppRowView row, AppUsage app)
        {
            row.Button.Tag = app;
            string name = string.IsNullOrWhiteSpace(app.Name) ? "未识别应用" : app.Name;
            string color = AppColors[(int)((uint)StableHash(app.AppId ?? name) % AppColors.Length)];
            row.NameText.Text = name;
            row.NameText.ToolTip = name;
            bool legacy = _summary?.HasLegacyActivity == true;
            row.Share = AppShare(app);
            row.DurationText.Text = _showRawForeground ? FormatForegroundDuration(app.ForegroundSeconds, legacy) : FormatDuration(app.Seconds);
            row.Button.ToolTip = name + "\n活跃 " + FormatDuration(app.Seconds) + "\n前台 " + FormatForegroundDuration(app.ForegroundSeconds, legacy) + "\n点击查看详情" + (legacy ? "\n" + LegacyActivityExplanation : "");
            if (row.PercentText != null) row.PercentText.Text = row.Share.ToString("P0", CultureInfo.CurrentCulture);
            if (row.Fill != null) row.Fill.Background = Brush(color);
            UpdateProgress(row);
            ImageSource? applicationIcon = null;
            try { applicationIcon = _controller.GetAppIcon(app.AppId ?? "unknown-app"); } catch { }
            if (applicationIcon != null)
            {
                if (!ReferenceEquals(row.IconSource, applicationIcon))
                {
                    row.Icon.Background = Brushes.Transparent;
                    row.Icon.Child = new Image { Source = applicationIcon, Width = 28, Height = 28, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    row.IconSource = applicationIcon;
                }
            }
            else if (row.IconSource != null || row.FallbackName != name)
            {
                row.IconSource = null;
                row.FallbackName = name;
                row.Icon.Background = Brush(color);
                row.Icon.Child = new TextBlock { Text = name.Substring(0, Math.Min(name.Length, name[0] <= 127 ? 2 : 1)).ToUpperInvariant(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
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
            chart.SetCoverage(day.ObservedHourly, day.UnknownHourly, future, expected);
        }

        private void SetWeekCoverage(UsageChart chart, List<DaySummary> days)
        {
            DateTime now = StatisticsNow();
            DateTime[] dates = days.Select(d => DateTime.TryParse(d.Date, out var date) ? date.Date : now.Date).ToArray();
            chart.SetCoverage(days.Select(d => d.ObservedSeconds).ToArray(), days.Select(d => d.UnknownSeconds).ToArray(), dates.Select(d => d > now.Date).ToArray(),
                dates.Select(d => d < now.Date ? 86400d : d > now.Date ? 0d : now.TimeOfDay.TotalSeconds).ToArray());
        }

        private void ShowAppDetails(AppUsage app, DateTime selectedDate, bool isWeek)
        {
            Safe(() =>
            {
                var days = _controller.GetWeek(selectedDate);
                var day = _controller.GetDay(selectedDate);
                var selected = isWeek ? Combine(days) : day;
                var selectedApp = selected.Apps.FirstOrDefault(a => string.Equals(a.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)) ?? app;
                var dialog = new Window { Owner = this, Title = app.Name + " · 使用详情", Width = 650, Height = 660, MinWidth = 560, MinHeight = 480, ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F7F8FC"), FontFamily = FontFamily, FontSize = 12 };
                var panel = new StackPanel { Margin = new Thickness(28) };
                panel.Children.Add(new TextBlock { Text = app.Name, FontSize = 23, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                string range = isWeek ? selectedDate.AddDays(-6).ToString("yyyy/M/d") + " — " + selectedDate.ToString("M/d") : selectedDate.ToString("yyyy/M/d");
                panel.Children.Add(new TextBlock { Text = range, Foreground = Brush("#929AAF"), FontSize = 11, Margin = new Thickness(0, 9, 0, 12) });
                panel.Children.Add(new TextBlock { Text = "活跃 " + FormatDuration(selectedApp.Seconds), FontSize = 21, Foreground = Brush("#5854E8"), FontWeight = FontWeights.SemiBold });
                panel.Children.Add(new TextBlock { Text = "前台 " + FormatForegroundDuration(selectedApp.ForegroundSeconds, selected.HasLegacyActivity) + " · 包含空闲", FontSize = 13, Margin = new Thickness(0, 7, 0, 0) });
                if (selected.HasLegacyActivity) panel.Children.Add(new TextBlock { Text = LegacyActivityExplanation, FontSize = 10, Foreground = Brush("#929AAF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                panel.Children.Add(new TextBlock { Text = "此应用的空闲规则", FontSize = 12, Margin = new Thickness(0, 20, 0, 8) });
                var idleSelector = new ComboBox { Width = 225, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("IdleRuleSelector") };
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
                panel.Children.Add(idleSelector);
                panel.Children.Add(new TextBlock { Text = "仅在启用空闲过滤时生效，只影响之后的记录。始终计时仍不包含后台、锁屏或关屏。", FontSize = 10, Foreground = Brush("#929AAF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                panel.Children.Add(new TextBlock { Text = selectedDate.ToString("M 月 d 日") + " · " + MetricName + "时长 · 按小时查看", FontSize = 12, Margin = new Thickness(0, 24, 0, 10) });
                if (day.HasHourlyDetails)
                {
                    var hours = _showRawForeground ? day.AppForegroundHourly : day.AppHourly;
                    double[] hourly = hours.TryGetValue(app.AppId, out var appHours) ? appHours : new double[24];
                    var dayChart = new UsageChart { Height = 142, ForegroundOnly = true, MetricLabel = MetricName, MetricIsLowerBound = _showRawForeground && day.HasLegacyActivity };
                    dayChart.SetData(Array.Empty<double>(), hourly, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(), day.HasData, "该日期未记录，不能视为零使用");
                    SetHourlyCoverage(dayChart, day, selectedDate);
                    panel.Children.Add(dayChart);
                }
                else panel.Children.Add(new TextBlock { Text = "该日期的小时明细已过保留期，仍可查看每日汇总。", Foreground = Brush("#939BB0"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 25) });
                panel.Children.Add(new TextBlock { Text = "最近七天 · " + MetricName + "时长", FontSize = 12, Margin = new Thickness(0, 24, 0, 10) });
                var chart = new UsageChart { Height = 125, ForegroundOnly = true, MetricLabel = MetricName, MetricIsLowerBound = _showRawForeground && days.Any(d => d.HasLegacyActivity) };
                double[] values = days.Select(d => d.Apps.Where(a => string.Equals(a.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)).Sum(AppSeconds)).ToArray();
                chart.SetData(Array.Empty<double>(), values, days.Select(d => DateTime.TryParse(d.Date, out var dt) ? dt.ToString("M/d") : d.Date).ToArray(), days.Any(d => d.HasData));
                SetWeekCoverage(chart, days);
                panel.Children.Add(chart);
                panel.Children.Add(new TextBlock { Text = "前台时长包含空闲；活跃时长按记录时的空闲规则估算。灰纹为未记录/部分记录，浅底为未来时间，均不能解释为零使用。", Foreground = Brush("#9CA4B6"), FontSize = 10, Margin = new Thickness(0, 17, 0, 0), TextWrapping = TextWrapping.Wrap });
                ThemeManager.Apply(dialog, _controller.DarkMode);
                dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                dialog.ShowDialog();
            });
        }

        public static string FormatDuration(double seconds)
        {
            seconds = Math.Max(0, seconds);
            if (seconds < 60) return ((int)Math.Floor(seconds)).ToString(CultureInfo.InvariantCulture) + "秒";
            int minutes = (int)Math.Floor(seconds / 60);
            return minutes < 60 ? minutes + "分" + ((int)Math.Floor(seconds) % 60).ToString("00", CultureInfo.InvariantCulture) + "秒" : minutes / 60 + "小时" + minutes % 60 + "分";
        }

        private string RangeText() => _isWeek ? _selectedDate.AddDays(-6).ToString("yyyy/M/d") + " — " + _selectedDate.ToString("M/d") : _selectedDate.ToString("yyyy/M/d");
        private static Brush Brush(string color) => ThemeManager.LegacyBrush(color);
        private void SetNav(Button button, bool active) { button.Background = Brush(active ? "#303B5B" : "#19233E"); button.Foreground = active ? Brushes.White : ThemeManager.GetBrush("SidebarMuted"); }
        private void SetDateButton(Button button, bool active) { button.Background = Brush(active ? "#EFEEFF" : "#FFFFFF"); button.Foreground = Brush(active ? "#5854E8" : "#717C93"); button.BorderBrush = Brush(active ? "#DAD7FD" : "#E5E8F0"); }

        private void Nav_Click(object sender, RoutedEventArgs e) { _page = (sender as Button)?.Tag as string ?? "overview"; Render(); }
        private void AllApps_Click(object sender, RoutedEventArgs e) { _page = "apps"; Render(); }
        private void Today_Click(object sender, RoutedEventArgs e) => SelectDate(_controller.Today, false);
        private void Yesterday_Click(object sender, RoutedEventArgs e) => SelectDate(_controller.Today.AddDays(-1), false);
        private void Week_Click(object sender, RoutedEventArgs e) => SelectDate(_controller.Today, true);
        private void ActiveMetric_Click(object sender, RoutedEventArgs e) { _showRawForeground = false; Render(); }
        private void ForegroundMetric_Click(object sender, RoutedEventArgs e) { _showRawForeground = true; Render(); }
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
        private void IdleTimeout_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && !_rendering && int.TryParse(IdleTimeoutSelector.SelectedValue as string, out int minutes))
                Safe(() => _controller.IdleTimeoutMinutes = minutes);
        }
        private void DarkMode_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.DarkMode = DarkModeCheck.IsChecked == true); }
        private void Topmost_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AlwaysOnTop = TopmostCheck.IsChecked == true); }
        private void Exit_Click(object sender, RoutedEventArgs e) => _controller.Exit();
        private void Refresh_Click(object sender, RoutedEventArgs e) => Safe(() => { _controller.Refresh(); Render(); });
        private void Start_Click(object sender, RoutedEventArgs e) => StartRecording();
        private void StartRecording() => Safe(() =>
        {
            _controller.StartRecording();
            try { _controller.AutoStart = WelcomeAutoStart.IsChecked == true; }
            catch (Exception ex) { MessageBox.Show(this, "已经开始记录，但自启动设置未完成：\n" + ex.Message + "\n\n你可以稍后在设置中重试。", "自启动设置", MessageBoxButton.OK, MessageBoxImage.Information); }
            Render();
        });
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
                Safe(() => { _controller.DeleteRange(_isWeek ? _selectedDate.AddDays(-6) : _selectedDate, _selectedDate); Render(); });
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定删除所有已有的使用记录？\n\n明细、每日汇总及本程序管理的备份都会清除，无法撤销。已导出的文件不会删除。\n\n之后是否继续记录取决于当前记录开关。", "删除全部记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                Safe(() => { _controller.DeleteAll(); Render(); });
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
