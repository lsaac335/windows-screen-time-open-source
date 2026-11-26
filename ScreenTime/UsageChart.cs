using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenTime
{
    public sealed class UsageChart : FrameworkElement
    {
        private double[] _screen = Array.Empty<double>();
        private double[] _foreground = Array.Empty<double>();
        private string[] _labels = Array.Empty<string>();
        private double[] _observed = Array.Empty<double>();
        private double[]? _screenObserved;
        private double[] _unknown = Array.Empty<double>();
        private double[] _expected = Array.Empty<double>();
        private bool[] _future = Array.Empty<bool>();
        private string _emptyText = "还没有记录，开始后时间会在这里呈现";
        private bool _hasData;
        private const double AxisWidth = 45;
        private const double PlotRightInset = 4;
        private readonly Typeface _face = new Typeface("Microsoft YaHei UI");
        public bool ForegroundOnly { get; set; }
        public string MetricLabel { get; set; } = "活跃";
        public bool MetricIsLowerBound { get; set; }
        public UsageChart()
        {
            MouseMove += OnMove;
            MouseLeave += (_, _) => ToolTip = null;
        }

        public void SetData(double[] screen, double[] foreground, string[] labels, bool hasData, string? emptyText = null)
        {
            _screen = screen ?? Array.Empty<double>();
            _foreground = foreground ?? Array.Empty<double>();
            _labels = labels ?? Array.Empty<string>();
            _hasData = hasData;
            _emptyText = emptyText ?? "还没有记录，开始后时间会在这里呈现";
            InvalidateVisual();
        }

        public void SetCoverage(double[] observed, double[] unknown, bool[]? future = null, double[]? expected = null, double[]? screenObserved = null)
        {
            _observed = observed ?? Array.Empty<double>();
            _screenObserved = screenObserved;
            _unknown = unknown ?? Array.Empty<double>();
            _future = future ?? Array.Empty<bool>();
            _expected = expected ?? Array.Empty<double>();
            InvalidateVisual();
        }

        private bool IsFuture(int index) => index < _future.Length && _future[index];
        private static double At(double[] values, int index) => index < values.Length ? Math.Max(0, values[index]) : 0;
        private double Observed(int index) => At(_observed, index);
        private double Expected(int index) => index < _expected.Length ? Math.Max(0, _expected[index]) : _screen.Length == 24 || _foreground.Length == 24 ? 3600 : 86400;
        private bool HasCoverage => _observed.Length > 0;
        private bool HasMissing(int index) => HasCoverage && !IsFuture(index) && (Observed(index) + 2 < Expected(index) || (index < _unknown.Length && _unknown[index] > 0));
        private int PeriodCount => Math.Max(_labels.Length, Math.Max(_screen.Length, _foreground.Length));

        private static Brush HatchBrush()
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(ThemeManager.GetBrush("ChartMissing"), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            drawing.Children.Add(new GeometryDrawing(null, new Pen(ThemeManager.GetBrush("ChartHatch"), .8), new LineGeometry(new Point(0, 8), new Point(8, 0))));
            var brush = new DrawingBrush(drawing) { Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Stretch = Stretch.None };
            brush.Freeze();
            return brush;
        }

        private FormattedText Text(string text, double size, Brush brush) => new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, _face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 40) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            const double bottom = 21, top = 8;
            double cw = w - AxisWidth - PlotRightInset, ch = h - bottom - top;
            var gridPen = new Pen(ThemeManager.GetBrush("ChartGrid"), 1);
            double maximum = Math.Max(1, _screen.Concat(_foreground).DefaultIfEmpty(0).Max());
            double scale = maximum <= 60 ? 60 : maximum <= 300 ? 300 : maximum <= 900 ? 900 : maximum <= 1800 ? 1800 : maximum <= 3600 ? 3600 : Math.Ceiling(maximum / 3600) * 3600;
            for (int line = 0; line < 3; line++)
            {
                double y = top + ch * line / 2;
                dc.DrawLine(gridPen, new Point(AxisWidth, y), new Point(w - PlotRightInset, y));
                double val = scale * (2 - line) / 2;
                string txt = AxisLabel(val);
                dc.DrawText(Text(txt, 11, ThemeManager.GetBrush("ChartAxis")), new Point(0, y - 7));
            }
            int count = PeriodCount == 0 ? 24 : PeriodCount;
            double slot = cw / count;
            var screenPen = new Pen(ThemeManager.GetBrush("ChartScreenStroke"), 1);
            var metricBrush = ThemeManager.GetBrush("ChartForeground");
            Brush hatch = HatchBrush();
            for (int i = 0; i < count; i++)
            {
                double slotLeft = AxisWidth + i * slot;
                double center = slotLeft + slot / 2;
                var periodBounds = new Rect(slotLeft, top, slot, ch);
                if (HasMissing(i)) dc.DrawRectangle(hatch, null, periodBounds);
                else if (IsFuture(i)) dc.DrawRectangle(ThemeManager.GetBrush("ChartFuture"), null, periodBounds);
                if (_hasData && !IsFuture(i))
                {
                    double width = Math.Min(29, slot * (ForegroundOnly ? .60 : .34));
                    double gap = Math.Max(1, Math.Min(4, slot * .10)) / 2;
                    if (!ForegroundOnly)
                        DrawBar(dc, center - gap - width, top + ch, width, ch * At(_screen, i) / scale, null, screenPen);
                    double metricLeft = ForegroundOnly ? center - width / 2 : center + gap;
                    DrawBar(dc, metricLeft, top + ch, width, ch * At(_foreground, i) / scale, metricBrush, null);
                }
                int labelStep = slot < 13 ? 6 : 3;
                bool drawLabel = count <= 7 || i % labelStep == 0 || i == count - 1;
                if (drawLabel)
                {
                    FormattedText ft = Text(PeriodLabel(i), 11, ThemeManager.GetBrush("ChartAxis"));
                    dc.DrawText(ft, new Point(Math.Min(w - ft.Width, center - ft.Width / 2), h - 16));
                }
            }
            if (!_hasData)
            {
                var ft = Text(_emptyText, 11, ThemeManager.GetBrush("Muted"));
                ft.MaxTextWidth = Math.Max(60, cw - 25);
                dc.DrawRoundedRectangle(ThemeManager.GetBrush("CardBackground"), null, new Rect(AxisWidth + 5, top + ch / 2 - 19, cw - 10, 38), 8, 8);
                dc.DrawText(ft, new Point(AxisWidth + Math.Max(8, (cw - ft.Width) / 2), top + ch / 2 - ft.Height / 2));
            }
        }

        private static string AxisLabel(double seconds)
        {
            if (seconds >= 3600) return (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture) + "时";
            if (seconds >= 60) return (seconds / 60).ToString("0.#", CultureInfo.InvariantCulture) + "分";
            return seconds == 0 ? "0" : seconds.ToString("0", CultureInfo.InvariantCulture) + "秒";
        }

        private static void DrawBar(DrawingContext drawing, double left, double baseline, double width, double height, Brush? fill, Pen? outline)
        {
            if (height <= 0 || width < 1) return;
            height = Math.Max(2, height);
            double corner = Math.Min(2, width / 3);
            drawing.DrawRoundedRectangle(fill, outline, new Rect(left, baseline - height, width, height), corner, corner);
        }

        private string PeriodLabel(int index) => index < _labels.Length ? _labels[index] : index.ToString("00", CultureInfo.InvariantCulture);

        private string DescribePeriod(int index)
        {
            string heading = PeriodCount == 24 ? PeriodLabel(index) + ":00 – " + (index + 1).ToString("00") + ":00" : PeriodLabel(index);
            if (IsFuture(index)) return heading + "\n未来时段";
            if (!_hasData && !HasCoverage) return heading + "\n" + _emptyText;

            double observed = Observed(index);
            bool metricMissing = HasCoverage && observed <= .01 && At(_foreground, index) <= 0;
            var screenCoverage = _screenObserved ?? _observed;
            bool screenMissing = screenCoverage.Length > 0 && At(screenCoverage, index) <= .01 && At(_screen, index) <= 0;
            string screen = screenMissing ? "未记录" : MainWindow.FormatDuration(At(_screen, index));
            string metric = metricMissing ? "未记录" : (MetricIsLowerBound ? "≥ " : "") + MainWindow.FormatDuration(At(_foreground, index));
            string values = ForegroundOnly ? "" : "亮屏 " + screen + "    ";
            string coverage = !HasCoverage ? "" : metricMissing ? "\n未记录时段不计为零使用" :
                (HasMissing(index) ? "\n部分记录 · 已观测 " : "\n已观测 ") + MainWindow.FormatDuration(observed);
            return heading + "\n" + values + MetricLabel + " " + metric + coverage +
                (MetricIsLowerBound && !metricMissing ? "\n前台时长按旧记录可确认的下限显示" : "");
        }

        public void ShowDetails(string title)
        {
            var periods = !_hasData && !HasCoverage || PeriodCount == 0
                ? new[] { _emptyText }
                : Enumerable.Range(0, PeriodCount).Select(DescribePeriod).ToArray();
            var dialog = new ChartDetailsWindow(title, periods, ThemeManager.IsDark);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if ((!_hasData && !HasCoverage) || PeriodCount == 0 || ActualWidth <= AxisWidth + PlotRightInset) return;
            double position = (e.GetPosition(this).X - AxisWidth) / (ActualWidth - AxisWidth - PlotRightInset);
            ToolTip = position < 0 || position >= 1 ? null : DescribePeriod((int)(position * PeriodCount));
        }
    }
}
