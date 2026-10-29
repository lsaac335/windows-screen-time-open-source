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
        private bool _compact = true;
        private bool _isWeek;
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
            SetExpanded(true);
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
                    _rowCaches.Clear(); TopAppsHost.Children.Clear(); AllAppsHost.Children.Clear(); CompactAppsHost.Children.Clear();
                }
                Topmost = _compact && _controller.AlwaysOnTop;
                PinButton.IsChecked = _controller.AlwaysOnTop;
                PinButton.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
                PinButton.ToolTip = _controller.AlwaysOnTop ? "取消小窗置顶" : "置顶小窗";
                System.Windows.Automation.AutomationProperties.SetName(PinButton, _controller.AlwaysOnTop ? "取消小窗置顶" : "置顶小窗");
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
                if (_compact)
                {
                    RenderCompact(enabled, paused, warning, statusColor);
                    return;
                }
                AutoStartCheck.IsChecked = _controller.AutoStart;
                AutoRecordCheck.IsChecked = _controller.AutoRecord;
                AutoStartWidgetCheck.IsChecked = _controller.AutoStartWidget;
                IdleFilterCheck.IsChecked = _controller.IdleFilterEnabled;
                ForegroundRuleText.Text = _controller.IdleFilterEnabled
                    ? "已开启空闲过滤：连续 5 分钟无键鼠操作后，仅累计亮屏。阅读、视频可能触发，可在设置关闭。"
                    : "未开启空闲过滤：阅读、看视频时无需操作键鼠，前台时间也会正常累计。";
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
                LockedValue.Text = summary.HasData ? FormatDuration(summary.LockedSeconds) : "—";
                if (_isWeek)
                {
                    ChartTitle.Text = "最近七天的使用分布";
                    ChartCaption.Text = RangeText() + " · 按天汇总";
                    MainChart.SetData(_week.Select(d => d.ScreenSeconds).ToArray(), _week.Select(d => d.ForegroundSeconds).ToArray(),
                        _week.Select(d => DateTime.TryParse(d.Date, out var date) ? date.ToString("M/d") : d.Date).ToArray(), summary.HasData);
                    SetWeekCoverage(MainChart, _week);
                    int valid = _week.Count(d => d.HasData);
                    CoverageText.Text = valid == 0 ? "这段时间还没有可用记录，灰纹表示未记录。" : "已记录 " + valid + "/7 天。灰纹为未记录或部分记录的日期，不能视为完整全天数据。";
                }
                else
                {
                    ChartTitle.Text = "一天中的使用分布";
                    ChartCaption.Text = _selectedDate.ToString("M 月 d 日") + " · 每小时累计";
                    bool detail = summary.HasHourlyDetails;
                    MainChart.SetData(summary.ScreenHourly, summary.ForegroundHourly, Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToArray(), summary.HasData && detail,
                        summary.HasData && !detail ? "小时明细已过保留期，仍可查看每日汇总" : "还没有记录，开始后时间会在这里呈现");
                    if (detail) SetHourlyCoverage(MainChart, summary, _selectedDate);
                    else MainChart.SetCoverage(Array.Empty<double>(), Array.Empty<double>());
                    CoverageText.Text = !summary.HasData ? "无记录 · 首次启用前、暂停与未运行的区间不会补记为零使用。" :
                        (summary.FirstRecorded.HasValue ? "当日自 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(summary.FirstRecorded.Value, _controller.TimeZoneId).ToString("HH:mm") + " 开始记录" : "已记录当前日期的一部分") +
                        (summary.HasGaps ? " · 数据不完整，灰纹为未记录/部分记录，浅底为未来时间。" : " · 仅汇总实际观测到的区间。");
                }
                RenderTopApps(summary.Apps);
                RenderAllApps(summary.Apps);
            }
            catch (Exception ex)
            {
                ErrorBanner.Visibility = Visibility.Visible;
                ErrorText.Text = "读取记录时遇到问题：" + ex.Message;
                CompactError.Visibility = Visibility.Visible;
                CompactErrorText.Text = "读取记录时遇到问题：" + ex.Message;
            }
            finally { _rendering = false; }
        }

        private void RenderCompact(bool enabled, bool paused, bool warning, Brush statusColor)
        {
            DateTime today = _controller.Today;
            DaySummary day = _controller.GetDay(today);
            CompactDateText.Text = today.ToString("M 月 d 日  dddd", CultureInfo.GetCultureInfo("zh-CN")) + " · 今天";
            CompactStatusText.Text = StatusLabel.Text;
            CompactStatusText.Foreground = statusColor;
            CompactStatusDot.Fill = statusColor;
            CompactCurrentApp.Text = !enabled ? "从现在开始，了解你的时间。" : paused ? "已暂停，记录不会继续增长。" : CurrentAppText.Text + " · " + _controller.DisplayStateText;
            CompactCurrentApp.ToolTip = CompactCurrentApp.Text;
            CompactPauseButton.Content = !enabled ? "开始记录" : paused ? "▶ 恢复" : "Ⅱ 暂停";
            CompactWelcome.Visibility = !enabled ? Visibility.Visible : Visibility.Collapsed;
            CompactError.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
            CompactErrorText.Text = ErrorText.Text;
            CompactScreenValue.Text = day.HasData ? FormatDuration(day.ScreenSeconds) : "—";
            CompactForegroundValue.Text = day.HasData ? FormatDuration(day.ForegroundSeconds) : "—";
            CompactChart.SetData(day.ScreenHourly, day.ForegroundHourly, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(), day.HasData, "还没有记录");
            SetHourlyCoverage(CompactChart, day, today);
            string started = day.FirstRecorded.HasValue ? "自 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(day.FirstRecorded.Value, _controller.TimeZoneId).ToString("HH:mm") + " 开始 · " : "";
            CompactCoverageText.Text = started + (day.HasData ? "灰纹为未记录，浅底为未来时间" : "未记录区间不代表零使用");
            CompactCoverageText.ToolTip = CompactCoverageText.Text;
            UpdateAppList(CompactAppsHost, day.Apps.Take(3).ToList(), false, "还没有应用记录", "开始后切换软件，就会自动出现在这里。", true);
        }

        private static DaySummary Combine(List<DaySummary> days)
        {
            var apps = days.SelectMany(d => d.Apps).GroupBy(a => a.AppId).Select(g => new AppUsage { AppId = g.Key, Name = g.Last().Name, Seconds = g.Sum(a => a.Seconds) }).OrderByDescending(a => a.Seconds).ToList();
            double foreground = days.Sum(d => d.ForegroundSeconds);
            foreach (var app in apps) app.Share = foreground > 0 ? app.Seconds / foreground : 0;
            return new DaySummary
            {
                ScreenSeconds = days.Sum(d => d.ScreenSeconds), ForegroundSeconds = foreground, LockedSeconds = days.Sum(d => d.LockedSeconds),
                ObservedSeconds = days.Sum(d => d.ObservedSeconds), UnknownSeconds = days.Sum(d => d.UnknownSeconds), Apps = apps,
                HasData = days.Any(d => d.HasData), HasGaps = days.Any(d => d.HasGaps) || days.Count(d => d.HasData) < 7
            };
        }

        private void RenderTopApps(List<AppUsage> apps)
        {
            UpdateAppList(TopAppsHost, apps.Take(5).ToList(), false, "应用使用记录会出现在这里", "开始记录后，试着切换几个软件。");
        }

        private void RenderAllApps(List<AppUsage> apps)
        {
            string search = SearchBox.Text?.Trim() ?? "";
            var filtered = apps.Where(a => a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(a => a.Seconds).ToList();
            AppCountText.Text = (search.Length > 0 ? "匹配 " + filtered.Count + " / " : "") + "共 " + apps.Count + " 个应用与系统条目 · 按前台时长排序";
            AppBarCaptionText.Text = RangeText() + " · " + (filtered.Count == 0 ? (search.Length > 0 ? "没有匹配的应用" : "暂无应用记录") : "按时长排序 · 显示前 " + Math.Min(AppUsageBarChart.MaximumRows, filtered.Count) + " 个");
            AppsBarChart.SetApps(filtered, search.Length > 0 ? "没有匹配的应用，试试其他名称。" : "这段时间还没有应用使用记录。开启记录后切换几个软件即可查看。");
            AppBarCoverageText.Text = "柱条从零起按时长比较，占比按所选范围全部应用计算，不含本软件。点击柱条查看详情。" +
                (_summary?.HasGaps == true ? " 仅含实际已记录区间，未记录时段不代表零使用。" : "");
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
            if (row.Fill != null && row.Track != null && row.Button.Tag is AppUsage current)
                row.Fill.Width = Math.Max(0, row.Track.ActualWidth * Math.Clamp(current.Share, 0, 1));
        }

        private void UpdateAppRow(AppRowView row, AppUsage app)
        {
            row.Button.Tag = app;
            string name = string.IsNullOrWhiteSpace(app.Name) ? "未识别应用" : app.Name;
            string color = AppColors[(int)((uint)StableHash(app.AppId ?? name) % AppColors.Length)];
            row.NameText.Text = name;
            row.NameText.ToolTip = name;
            row.DurationText.Text = FormatDuration(app.Seconds);
            row.Button.ToolTip = "查看 " + name + " 的使用详情";
            if (row.PercentText != null) row.PercentText.Text = app.Share.ToString("P0", CultureInfo.CurrentCulture);
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
                var dialog = new Window { Owner = this, Title = app.Name + " · 使用详情", Width = 650, Height = 660, MinWidth = 560, MinHeight = 480, ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F7F8FC"), FontFamily = FontFamily, FontSize = 12 };
                var panel = new StackPanel { Margin = new Thickness(28) };
                panel.Children.Add(new TextBlock { Text = app.Name, FontSize = 23, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                string range = isWeek ? selectedDate.AddDays(-6).ToString("yyyy/M/d") + " — " + selectedDate.ToString("M/d") : selectedDate.ToString("yyyy/M/d");
                panel.Children.Add(new TextBlock { Text = range + " · 前台使用时长", Foreground = Brush("#929AAF"), FontSize = 11, Margin = new Thickness(0, 9, 0, 12) });
                panel.Children.Add(new TextBlock { Text = FormatDuration(app.Seconds) + "  ·  " + app.Share.ToString("P0") + " 的前台时间", FontSize = 19, Foreground = Brush("#5854E8"), FontWeight = FontWeights.SemiBold });
                panel.Children.Add(new TextBlock { Text = selectedDate.ToString("M 月 d 日") + " · 按小时查看", FontSize = 12, Margin = new Thickness(0, 24, 0, 10) });
                if (day.HasHourlyDetails)
                {
                    double[] hourly = day.AppHourly.TryGetValue(app.AppId, out var appHours) ? appHours : new double[24];
                    var dayChart = new UsageChart { Height = 142, ForegroundOnly = true };
                    dayChart.SetData(Array.Empty<double>(), hourly, Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray(), day.HasData, "该日期未记录，不能视为零使用");
                    SetHourlyCoverage(dayChart, day, selectedDate);
                    panel.Children.Add(dayChart);
                }
                else panel.Children.Add(new TextBlock { Text = "该日期的小时明细已过保留期，仍可查看每日汇总。", Foreground = Brush("#939BB0"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 25) });
                panel.Children.Add(new TextBlock { Text = "最近七天", FontSize = 12, Margin = new Thickness(0, 24, 0, 10) });
                var chart = new UsageChart { Height = 125, ForegroundOnly = true };
                double[] values = days.Select(d => d.Apps.Where(a => a.AppId == app.AppId).Sum(a => a.Seconds)).ToArray();
                chart.SetData(Array.Empty<double>(), values, days.Select(d => DateTime.TryParse(d.Date, out var dt) ? dt.ToString("M/d") : d.Date).ToArray(), values.Any(v => v > 0));
                SetWeekCoverage(chart, days);
                panel.Children.Add(chart);
                panel.Children.Add(new TextBlock { Text = "只统计有前台焦点的时间。灰纹为未记录/部分记录，浅底为未来时间，均不能解释为零使用。", Foreground = Brush("#9CA4B6"), FontSize = 10, Margin = new Thickness(0, 17, 0, 0), TextWrapping = TextWrapping.Wrap });
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
        private void SelectDate(DateTime date, bool week) { _selectedDate = date.Date; _isWeek = week; _followToday = week || _selectedDate == _controller.Today; _ready = false; DateSelector.SelectedDate = _selectedDate; _ready = true; Render(); }
        private void Date_Changed(object? sender, SelectionChangedEventArgs e) { if (_ready && !_rendering && DateSelector.SelectedDate.HasValue) { _selectedDate = DateSelector.SelectedDate.Value.Date; _isWeek = false; _followToday = _selectedDate == _controller.Today; Render(); } }
        private void Expand_Click(object sender, RoutedEventArgs e) => SetExpanded(true);
        private void Collapse_Click(object sender, RoutedEventArgs e) => SetExpanded(false);
        private void CompactAllApps_Click(object sender, RoutedEventArgs e) { _page = "apps"; SelectDate(_controller.Today, false); SetExpanded(true); }
        private void Pin_Click(object sender, RoutedEventArgs e) => Safe(() => { _controller.AlwaysOnTop = !_controller.AlwaysOnTop; Render(); });
        private void DockMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void DockMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void DockClose_Click(object sender, RoutedEventArgs e) => Close();
        private void CompactSettings_Click(object sender, RoutedEventArgs e) { _page = "settings"; SetExpanded(true); }
        private void Widget_Click(object sender, RoutedEventArgs e) => Safe(() => WidgetRequested?.Invoke());
        private void WidgetMode_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.WidgetEnabled = WidgetModeCheck.IsChecked == true); }
        private void AutoRecord_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AutoRecord = AutoRecordCheck.IsChecked == true); }
        private void AutoStartWidget_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AutoStartWidget = AutoStartWidgetCheck.IsChecked == true); }
        private void IdleFilter_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.IdleFilterEnabled = IdleFilterCheck.IsChecked == true); }
        private void DarkMode_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.DarkMode = DarkModeCheck.IsChecked == true); }
        private void Topmost_Changed(object sender, RoutedEventArgs e) { if (_ready && !_rendering) Safe(() => _controller.AlwaysOnTop = TopmostCheck.IsChecked == true); }
        private void Exit_Click(object sender, RoutedEventArgs e) => _controller.Exit();
        private void CompactPause_Click(object sender, RoutedEventArgs e) => Safe(() => { if (!_controller.IsRecordingEnabled) _controller.StartRecording(); else _controller.TogglePause(); Render(); });
        private void SetExpanded(bool expanded)
        {
            if (_compact == !expanded) return;
            double centerX = Left + ActualWidth / 2;
            double centerY = Top + ActualHeight / 2;
            _compact = !expanded;
            WindowState = WindowState.Normal;
            MinWidth = expanded ? 960 : 740;
            MinHeight = expanded ? 680 : 370;
            var area = SystemParameters.WorkArea;
            Width = Math.Min(expanded ? 1160 : 820, area.Width);
            Height = Math.Min(expanded ? 830 : 410, area.Height);
            Left = Math.Max(area.Left, Math.Min(centerX - Width / 2, area.Right - Width));
            Top = Math.Max(area.Top, Math.Min(centerY - Height / 2, area.Bottom - Height));
            FullPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            CompactPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            Render();
        }
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
