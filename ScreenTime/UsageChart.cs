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
        private double[] _unknown = Array.Empty<double>();
        private double[] _expected = Array.Empty<double>();
        private bool[] _future = Array.Empty<bool>();
        private string _emptyText = "还没有记录，开始后时间会在这里呈现";
        private bool _hasData;
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

        public void SetCoverage(double[] observed, double[] unknown, bool[]? future = null, double[]? expected = null)
        {
            _observed = observed ?? Array.Empty<double>();
            _unknown = unknown ?? Array.Empty<double>();
            _future = future ?? Array.Empty<bool>();
            _expected = expected ?? Array.Empty<double>();
            InvalidateVisual();
        }

        private bool IsFuture(int index) => index < _future.Length && _future[index];
        private double Observed(int index) => index < _observed.Length ? Math.Max(0, _observed[index]) : 0;
        private double Expected(int index) => index < _expected.Length ? Math.Max(0, _expected[index]) : _screen.Length == 24 || _foreground.Length == 24 ? 3600 : 86400;
        private bool HasCoverage => _observed.Length > 0;
        private bool HasMissing(int index) => HasCoverage && !IsFuture(index) && (Observed(index) + 2 < Expected(index) || (index < _unknown.Length && _unknown[index] > 0));

        private static Brush HatchBrush()
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(ThemeManager.GetBrush("ChartMissing"), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            drawing.Children.Add(new GeometryDrawing(null, new Pen(ThemeManager.GetBrush("ChartGrid"), .8), new LineGeometry(new Point(0, 8), new Point(8, 0))));
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
            const double left = 35, bottom = 23, top = 5;
            double cw = w - left - 4, ch = h - bottom - top;
            var gridPen = new Pen(ThemeManager.GetBrush("ChartGrid"), 1);
            double maximum = Math.Max(1, _screen.Concat(_foreground).DefaultIfEmpty(0).Max());
            double scale = maximum <= 60 ? 60 : maximum <= 300 ? 300 : maximum <= 900 ? 900 : maximum <= 1800 ? 1800 : maximum <= 3600 ? 3600 : Math.Ceiling(maximum / 3600) * 3600;
            for (int line = 0; line < 3; line++)
            {
                double y = top + ch * line / 2;
                dc.DrawLine(gridPen, new Point(left, y), new Point(w, y));
                double val = scale * (2 - line) / 2;
                string txt = val >= 3600 ? (val / 3600).ToString("0.#", CultureInfo.InvariantCulture) + "h" : val >= 60 ? (val / 60).ToString("0", CultureInfo.InvariantCulture) + "m" : val.ToString("0", CultureInfo.InvariantCulture) + "s";
                dc.DrawText(Text(txt, 9, ThemeManager.GetBrush("ChartAxis")), new Point(0, y - 6));
            }
            int count = Math.Max(_screen.Length, _foreground.Length);
            if (count == 0) count = 24;
            double slot = cw / count;
            var pale = ThemeManager.GetBrush("ChartScreen");
            var ink = ThemeManager.GetBrush("ChartForeground");
            Brush hatch = HatchBrush();
            for (int i = 0; i < count; i++)
            {
                double x = left + i * slot + slot * 0.17;
                double bw = Math.Max(2, slot * 0.66);
                double sv = i < _screen.Length ? Math.Max(0, _screen[i]) : 0;
                double fv = i < _foreground.Length ? Math.Max(0, _foreground[i]) : 0;
                if (HasMissing(i)) dc.DrawRectangle(hatch, null, new Rect(left + i * slot + 1, top, Math.Max(1, slot - 2), ch));
                else if (IsFuture(i)) dc.DrawRectangle(ThemeManager.GetBrush("ChartFuture"), null, new Rect(left + i * slot + 1, top, Math.Max(1, slot - 2), ch));
                if (_hasData && sv > 0)
                {
                    double bh = Math.Max(2, Math.Min(ch, sv / scale * ch));
                    dc.DrawRoundedRectangle(pale, null, new Rect(x, top + ch - bh, bw, bh), Math.Min(3, bw / 3), Math.Min(3, bw / 3));
                }
                if (_hasData && fv > 0)
                {
                    double bh = Math.Max(2, Math.Min(ch, fv / scale * ch));
                    dc.DrawRoundedRectangle(ink, null, new Rect(x, top + ch - bh, bw, bh), Math.Min(3, bw / 3), Math.Min(3, bw / 3));
                }
                bool drawLabel = count <= 7 || i % 3 == 0 || i == count - 1;
                if (drawLabel)
                {
                    string label = i < _labels.Length ? _labels[i] : i.ToString("00", CultureInfo.InvariantCulture);
                    FormattedText ft = Text(label, 9, ThemeManager.GetBrush("ChartAxis"));
                    dc.DrawText(ft, new Point(left + (i + .5) * slot - ft.Width / 2, h - 14));
                }
            }
            if (!_hasData)
            {
                var ft = Text(_emptyText, 11, ThemeManager.GetBrush("Muted"));
                ft.MaxTextWidth = Math.Max(60, cw - 25);
                dc.DrawRoundedRectangle(ThemeManager.GetBrush("CardBackground"), null, new Rect(left + 5, top + ch / 2 - 19, cw - 10, 38), 8, 8);
                dc.DrawText(ft, new Point(left + Math.Max(8, (cw - ft.Width) / 2), top + ch / 2 - ft.Height / 2));
            }
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_hasData && !HasCoverage) return;
            int count = Math.Max(_screen.Length, _foreground.Length);
            if (count == 0 || ActualWidth < 40) return;
            double position = (e.GetPosition(this).X - 35) / (ActualWidth - 39);
            int index = Math.Clamp((int)(position * count), 0, count - 1);
            double s = index < _screen.Length ? _screen[index] : 0;
            double f = index < _foreground.Length ? _foreground[index] : 0;
            string label = index < _labels.Length ? _labels[index] : index.ToString("00");
            string heading = label + (count == 24 ? ":00 — " + ((index + 1) % 24).ToString("00") + ":00" : "");
            if (IsFuture(index)) { ToolTip = heading + "\n尚未到来的时间"; return; }
            if (HasCoverage && Observed(index) <= .01) { ToolTip = heading + "\n未记录 · 不能视为零使用"; return; }
            string coverage = !HasCoverage ? "" : HasMissing(index) ? "\n部分记录 · 已观测 " + MainWindow.FormatDuration(Observed(index)) + "\n未记录区间不代表没有使用" : "\n已观测 " + MainWindow.FormatDuration(Observed(index));
            string metric = "\n" + MetricLabel + "  " + (MetricIsLowerBound ? "≥ " : "") + MainWindow.FormatDuration(f);
            ToolTip = heading + (ForegroundOnly ? metric : "\n亮屏  " + MainWindow.FormatDuration(s) + metric) + coverage +
                (MetricIsLowerBound ? "\n包含旧记录，此值为已知下限。" : "");
        }
    }
}
