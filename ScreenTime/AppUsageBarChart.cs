using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ScreenTime;

/// <summary>Comparable app durations on a shared, zero-based scale with application icons.</summary>
public sealed class AppUsageBarChart : UserControl
{
    public const int MaximumRows = 10;
    private static readonly string[] FallbackColors = { "#6373F2", "#34B89A", "#E5A04B", "#4A9CE8", "#D46E9E", "#7C8AA6" };

    private readonly StackPanel _rows = new();
    private readonly Grid _axis = Columns();
    private readonly TextBlock _middleTick = Tick();
    private readonly TextBlock _maximumTick = Tick();
    private readonly TextBlock _empty = new()
    {
        FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12, 28, 12, 28)
    };
    private readonly Dictionary<string, BarRow> _cache = new(StringComparer.OrdinalIgnoreCase);
    public event Action<AppUsage>? AppSelected;

    public AppUsageBarChart()
    {
        var panel = new StackPanel();
        var label = Tick(); label.Text = "应用"; label.HorizontalAlignment = HorizontalAlignment.Left;
        _axis.Children.Add(label);
        var scale = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        var zero = Tick(); zero.Text = "0"; zero.HorizontalAlignment = HorizontalAlignment.Left;
        _middleTick.HorizontalAlignment = HorizontalAlignment.Center;
        _maximumTick.HorizontalAlignment = HorizontalAlignment.Right;
        scale.Children.Add(zero); scale.Children.Add(_middleTick); scale.Children.Add(_maximumTick);
        Grid.SetColumn(scale, 1); _axis.Children.Add(scale);
        var heading = Tick(); heading.Text = "时长 / 占比"; heading.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(heading, 2); _axis.Children.Add(heading);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(_axis); panel.Children.Add(_rows); panel.Children.Add(_empty);
        Content = panel;
    }

    public void SetApps(IReadOnlyList<AppUsage> apps, string emptyMessage, bool showForeground = false,
        bool foregroundIsLowerBound = false, double? totalSeconds = null, bool showVisible = false,
        Func<string, ImageSource?>? iconProvider = null)
    {
        double Seconds(AppUsage app) => showVisible ? app.VisibleSeconds : showForeground ? app.ForegroundSeconds : app.Seconds;
        string metric = showVisible ? "可见" : showForeground ? "前台" : "活跃";
        bool lowerBound = showForeground && foregroundIsLowerBound;
        double total = totalSeconds ?? apps.Sum(Seconds);
        var shown = apps.OrderByDescending(Seconds).ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .Take(MaximumRows).ToList();
        _axis.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = emptyMessage;
        double maximum = ScaleMaximum(shown.Select(Seconds).DefaultIfEmpty(0).Max());
        _middleTick.Text = AxisDuration(maximum / 2);
        _maximumTick.Text = AxisDuration(maximum);

        var desired = new List<Button>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < shown.Count; index++)
        {
            var app = shown[index];
            if (!ids.Add(app.AppId)) continue;
            if (!_cache.TryGetValue(app.AppId, out var row)) _cache[app.AppId] = row = CreateRow();
            string name = string.IsNullOrWhiteSpace(app.Name) ? "未识别应用" : app.Name;
            row.Button.Tag = app;
            row.Rank.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
            row.Rank.SetResourceReference(TextBlock.ForegroundProperty, index < 3 ? "AccentText" : "Secondary");
            row.Rank.FontWeight = index < 3 ? FontWeights.SemiBold : FontWeights.Normal;
            row.Name.Text = name;
            double seconds = Seconds(app);
            double share = total > 0 ? Math.Clamp(seconds / total, 0, 1) : 0;
            string duration = lowerBound ? MainWindow.FormatForegroundDuration(seconds, true) : MainWindow.FormatDuration(seconds);
            row.Value.Text = duration + " · " + share.ToString("P0", CultureInfo.CurrentCulture);
            double fraction = Math.Clamp(seconds / maximum, 0, 1);
            row.FillColumn.Width = new GridLength(fraction, GridUnitType.Star);
            row.RemainderColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
            row.Fill.Opacity = Math.Max(0.45, 1.0 - index * 0.06);

            ImageSource? icon = null;
            if (iconProvider != null)
            {
                try { icon = iconProvider(app.AppId); } catch { }
            }
            if (icon != null)
            {
                if (!ReferenceEquals(row.IconSource, icon))
                {
                    row.IconBox.Background = Brushes.Transparent;
                    row.IconBox.Child = new Image { Source = icon, Width = 20, Height = 20, Stretch = Stretch.Uniform };
                    row.IconSource = icon;
                    row.FallbackName = "";
                }
            }
            else if (row.IconSource != null || row.FallbackName != name)
            {
                row.IconSource = null;
                row.FallbackName = name;
                string color = FallbackColors[(int)((uint)StableHash(app.AppId ?? name) % FallbackColors.Length)];
                row.IconBox.Background = ThemeManager.LegacyBrush(color);
                row.IconBox.Child = new TextBlock
                {
                    Text = name.Substring(0, Math.Min(name.Length, name[0] <= 127 ? 2 : 1)).ToUpperInvariant(),
                    Foreground = Brushes.White, FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
            }

            string description = name + "\n活跃 " + MainWindow.FormatDuration(app.Seconds) +
                "\n可见 " + MainWindow.FormatDuration(app.VisibleSeconds) +
                "\n前台 " + MainWindow.FormatForegroundDuration(app.ForegroundSeconds, foregroundIsLowerBound) +
                "\n占所选范围应用" + metric + "时长 " + share.ToString("P1", CultureInfo.CurrentCulture) + "\n点击查看详情" +
                (foregroundIsLowerBound ? "\n" + MainWindow.LegacyActivityExplanation : "");
            row.Button.ToolTip = description;
            row.Name.ToolTip = name;
            AutomationProperties.SetName(row.Button, name + "，" + metric + "时长 " + duration +
                "，占比 " + share.ToString("P1", CultureInfo.CurrentCulture) + (lowerBound ? "，按已知下限计算" : ""));
            desired.Add(row.Button);
        }
        // Keep existing buttons while only durations change, so a refresh cannot steal keyboard focus.
        for (int index = 0; index < desired.Count; index++)
        {
            if (index < _rows.Children.Count && ReferenceEquals(_rows.Children[index], desired[index])) continue;
            if (_rows.Children.Contains(desired[index])) _rows.Children.Remove(desired[index]);
            _rows.Children.Insert(index, desired[index]);
        }
        while (_rows.Children.Count > desired.Count) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        foreach (string id in _cache.Keys.Where(id => !ids.Contains(id)).ToArray()) _cache.Remove(id);
    }

    private sealed class BarRow
    {
        public Button Button = new();
        public TextBlock Rank = new();
        public Border IconBox = new();
        public TextBlock Name = new();
        public TextBlock Value = new();
        public Border Fill = new();
        public ColumnDefinition FillColumn = new();
        public ColumnDefinition RemainderColumn = new();
        public ImageSource? IconSource;
        public string FallbackName = "";
    }

    private BarRow CreateRow()
    {
        var row = new BarRow();
        var grid = Columns(); grid.MinHeight = 36;
        var nameGrid = new Grid { Margin = new Thickness(0, 0, 12, 0) };
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Rank = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        Typography.SetNumeralAlignment(row.Rank, FontNumeralAlignment.Tabular);

        row.IconBox = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(row.IconBox, 1);

        row.Name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        row.Name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        Grid.SetColumn(row.Name, 2);

        nameGrid.Children.Add(row.Rank); nameGrid.Children.Add(row.IconBox); nameGrid.Children.Add(row.Name);
        grid.Children.Add(nameGrid);

        var track = new Border { Height = 9, CornerRadius = new CornerRadius(4.5), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "ProgressTrack");
        var bar = new Grid();
        bar.ColumnDefinitions.Add(row.FillColumn); bar.ColumnDefinitions.Add(row.RemainderColumn);
        row.Fill = new Border { CornerRadius = new CornerRadius(4.5) };
        row.Fill.SetResourceReference(Border.BackgroundProperty, "ProgressBarBrush");
        bar.Children.Add(row.Fill); track.Child = bar;
        Grid.SetColumn(track, 1); grid.Children.Add(track);

        row.Value = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 0, 0) };
        Typography.SetNumeralAlignment(row.Value, FontNumeralAlignment.Tabular);
        row.Value.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetColumn(row.Value, 2); grid.Children.Add(row.Value);

        row.Button = new Button { Content = grid, Padding = new Thickness(6, 2, 6, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        row.Button.SetResourceReference(StyleProperty, "RowButton");
        row.Button.Click += (_, _) => { if (row.Button.Tag is AppUsage current) AppSelected?.Invoke(current); };
        return row;
    }

    private static int StableHash(string value)
    {
        unchecked { int result = 17; foreach (char c in value) result = result * 31 + c; return result; }
    }

    private static Grid Columns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        return grid;
    }

    private static TextBlock Tick()
    {
        var text = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Top };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        return text;
    }

    private static double ScaleMaximum(double maximum)
    {
        if (maximum <= 0) return 60;
        double step = maximum <= 60 ? 15 : maximum <= 300 ? 60 : maximum <= 3600 ? 300 : maximum <= 10800 ? 1800 : 3600;
        return Math.Ceiling(maximum / step) * step;
    }

    private static string AxisDuration(double seconds)
    {
        if (seconds >= 3600) return (seconds / 3600).ToString("0.#", CultureInfo.CurrentCulture) + "小时";
        if (seconds >= 60) return (seconds / 60).ToString("0.#", CultureInfo.CurrentCulture) + "分钟";
        return seconds.ToString("0.#", CultureInfo.CurrentCulture) + "秒";
    }
}
