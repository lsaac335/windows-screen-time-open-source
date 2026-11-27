using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenTime
{
    public sealed class UsageChart : FrameworkElement
    {
        private static readonly Typeface LabelFace = new("Microsoft YaHei UI");
        private static readonly Typeface NumberFace = new("Segoe UI, Microsoft YaHei UI");

        private double[] _screen = Array.Empty<double>();
        private double[] _foreground = Array.Empty<double>();
        private string[] _labels = Array.Empty<string>();
        private double[] _observed = Array.Empty<double>();
        private double[]? _screenObserved;
        private double[] _unknown = Array.Empty<double>();
        private double[] _expected = Array.Empty<double>();
        private bool[] _future = Array.Empty<bool>();
        private IReadOnlyList<string>? _periodNotes;
        private string _emptyText = "还没有记录，开始后时间会在这里呈现";
        private bool _hasData;
        private int _hoverIndex = -1;

        private bool? _cachedDark;
        private Pen? _screenPen;

        public bool ForegroundOnly { get; set; }
        public bool CompactWidgetMode { get; set; }
        public string MetricLabel { get; set; } = "活跃";
        public bool MetricIsLowerBound { get; set; }

        public UsageChart()
        {
            MouseMove += OnMove;
            MouseLeave += (_, _) =>
            {
                ToolTip = null;
                if (_hoverIndex != -1) { _hoverIndex = -1; InvalidateVisual(); }
            };
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

        public void SetPeriodNotes(IReadOnlyList<string>? notes) => _periodNotes = notes;

        private bool IsFuture(int index) => index < _future.Length && _future[index];
        private static double At(double[] values, int index) => index < values.Length ? Math.Max(0, values[index]) : 0;
        private double Observed(int index) => At(_observed, index);
        private double Expected(int index) => index < _expected.Length ? Math.Max(0, _expected[index]) : _screen.Length == 24 || _foreground.Length == 24 ? 3600 : 86400;
        private bool HasCoverage => _observed.Length > 0;
        private bool HasMissing(int index) => HasCoverage && !IsFuture(index) && (Observed(index) + 2 < Expected(index) || (index < _unknown.Length && _unknown[index] > 0));
        private int PeriodCount => Math.Max(_labels.Length, Math.Max(_screen.Length, _foreground.Length));

        // Group 24 hours into 12 plump 2-hour visual buckets for clean minimalist capsules,
        // or 8 plump buckets in the compact desktop widget.
        private int BucketStep => PeriodCount == 24 ? (CompactWidgetMode ? 3 : 2) : 1;
        private int VisualCount => PeriodCount == 0 ? 12 : (PeriodCount + BucketStep - 1) / BucketStep;

        private void EnsureResources()
        {
            bool dark = ThemeManager.IsDark;
            if (_cachedDark == dark && _screenPen != null) return;
            _cachedDark = dark;
            _screenPen = new Pen(ThemeManager.GetBrush("ChartScreenStroke"), 1);
            _screenPen.Freeze();
        }

        private FormattedText Text(string text, double size, Brush brush, bool numeric = false) => new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, numeric ? NumberFace : LabelFace, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private (double Screen, double Metric, bool AllFuture, int StartHour, int EndHour) ReadBucket(int bucket)
        {
            int step = BucketStep;
            int start = bucket * step;
            int end = Math.Min(PeriodCount, start + step);
            double scr = 0, met = 0;
            bool allFuture = true;
            for (int i = start; i < end; i++)
            {
                if (!IsFuture(i)) allFuture = false;
                scr += At(_screen, i);
                met += At(_foreground, i);
            }
            return (scr, met, allFuture, start, end);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 28) return;
            EnsureResources();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double bottom = CompactWidgetMode ? 2 : 22;
            double top = CompactWidgetMode ? 4 : 10;
            double leftPad = 2, rightPad = 2;
            double cw = w - leftPad - rightPad;
            double ch = Math.Max(12, h - bottom - top);

            int vCount = VisualCount;
            var buckets = new (double Screen, double Metric, bool AllFuture, int StartHour, int EndHour)[vCount];
            double maxVal = 0;
            bool parallelOverflow = false;
            for (int b = 0; b < vCount; b++)
            {
                buckets[b] = ReadBucket(b);
                maxVal = Math.Max(maxVal, Math.Max(buckets[b].Screen, buckets[b].Metric));
                if (!ForegroundOnly && buckets[b].Metric > buckets[b].Screen + 1) parallelOverflow = true;
            }
            double scale = maxVal <= 60 ? 60 : maxVal <= 300 ? 300 : maxVal <= 900 ? 900 : maxVal <= 1800 ? 1800 : maxVal <= 3600 ? 3600 : Math.Ceiling(maxVal / 1800) * 1800;

            double slot = cw / vCount;
            var screenFill = ThemeManager.GetBrush("ChartScreenFill");
            var metricBrush = ThemeManager.GetBrush("ChartForegroundBrush");
            var emptyPill = ThemeManager.GetBrush("ProgressTrack");
            var hoverBrush = ThemeManager.GetBrush("ChartHoverBand");
            var axisBrush = ThemeManager.GetBrush("ChartAxis");

            for (int b = 0; b < vCount; b++)
            {
                double slotLeft = leftPad + b * slot;
                double center = slotLeft + slot / 2.0;

                if (b == _hoverIndex)
                    dc.DrawRoundedRectangle(hoverBrush, null, new Rect(slotLeft + 1, top, Math.Max(2, slot - 2), ch), 8, 8);

                var item = buckets[b];
                double maxBarW = CompactWidgetMode ? 28 : (vCount <= 7 ? 34 : 22);
                double outerW = Math.Min(maxBarW, Math.Max(6, slot * (CompactWidgetMode ? 0.78 : 0.62)));

                if (_hasData && !item.AllFuture && (item.Screen > 0.5 || item.Metric > 0.5))
                {
                    if (ForegroundOnly)
                    {
                        DrawFullPill(dc, center - outerW / 2, top + ch, outerW, ch * item.Metric / scale, metricBrush, null);
                    }
                    else if (parallelOverflow)
                    {
                        double w2 = Math.Min(16, Math.Max(4, slot * 0.36));
                        double gap = Math.Max(2, slot * 0.08) / 2;
                        DrawFullPill(dc, center - gap - w2, top + ch, w2, ch * item.Screen / scale, screenFill, _screenPen);
                        DrawFullPill(dc, center + gap, top + ch, w2, ch * item.Metric / scale, metricBrush, null);
                    }
                    else
                    {
                        // Signature mockup overlaid full-pill capsules:
                        // Soft translucent indigo screen pill with glowing Indigo->Cyan pill inside!
                        double screenH = ch * item.Screen / scale;
                        double metricH = ch * item.Metric / scale;
                        if (CompactWidgetMode)
                        {
                            // Full-width nested pill just like the widget preview
                            DrawFullPill(dc, center - outerW / 2, top + ch, outerW, Math.Max(outerW * 0.45, screenH), screenFill, null);
                            if (item.Metric > 0.5)
                                DrawFullPill(dc, center - outerW / 2, top + ch, outerW, Math.Max(outerW * 0.42, metricH), metricBrush, null);
                        }
                        else
                        {
                            DrawFullPill(dc, center - outerW / 2, top + ch, outerW, screenH, screenFill, _screenPen);
                            double innerW = Math.Max(4, outerW * 0.76);
                            if (item.Metric > 0.5)
                                DrawFullPill(dc, center - innerW / 2, top + ch, innerW, metricH, metricBrush, null);
                        }
                    }
                }
                else
                {
                    // Subtle minimal baseline pill stub so empty periods keep clean rhythm without hatch noise
                    double stubH = CompactWidgetMode ? Math.Min(8, outerW * 0.36) : 5;
                    DrawFullPill(dc, center - outerW / 2, top + ch, outerW, stubH, emptyPill, null);
                }

                if (!CompactWidgetMode && ShouldDrawBucketLabel(b, vCount))
                {
                    string lbl = BucketLabel(b, item.StartHour);
                    FormattedText ft = Text(lbl, 11, b == _hoverIndex ? ThemeManager.GetBrush("Ink") : axisBrush, numeric: true);
                    double x = Math.Clamp(center - ft.Width / 2, leftPad, Math.Max(leftPad, w - ft.Width - rightPad));
                    dc.DrawText(ft, new Point(x, h - 17));
                }
            }

            if (!_hasData && !CompactWidgetMode)
            {
                var ft = Text(_emptyText, 12, ThemeManager.GetBrush("Muted"));
                ft.MaxTextWidth = Math.Max(60, cw - 32);
                var pill = new Rect(leftPad + Math.Max(8, (cw - ft.Width) / 2 - 16), top + ch / 2 - 19, Math.Min(cw - 16, ft.Width + 32), 38);
                dc.DrawRoundedRectangle(ThemeManager.GetBrush("CardAltBackground"), new Pen(ThemeManager.GetBrush("GlassCardEdge"), 1), pill, 12, 12);
                dc.DrawText(ft, new Point(pill.Left + (pill.Width - ft.Width) / 2, top + ch / 2 - ft.Height / 2));
            }
        }

        private static bool ShouldDrawBucketLabel(int bucket, int vCount)
        {
            if (vCount <= 7) return true;
            // 12 buckets (00, 02, 04, ..., 22) -> show 00, 04, 08, 12, 16, 20, 22
            return bucket % 2 == 0 || bucket == vCount - 1;
        }

        private string BucketLabel(int bucket, int startHour)
        {
            if (BucketStep == 1) return PeriodLabel(bucket);
            if (bucket == VisualCount - 1 && PeriodCount == 24) return "23";
            return startHour.ToString("00", CultureInfo.InvariantCulture);
        }

        private static void DrawFullPill(DrawingContext drawing, double left, double baseline, double width, double height, Brush? fill, Pen? outline)
        {
            if (height <= 0.1 || width < 1) return;
            height = Math.Max(Math.Min(width, 5.0), height);
            double corner = Math.Min(width / 2.0, height / 2.0);
            drawing.DrawRoundedRectangle(fill, outline, new Rect(left, baseline - height, width, height), corner, corner);
        }

        private string PeriodLabel(int index) => index < _labels.Length ? _labels[index] : index.ToString("00", CultureInfo.InvariantCulture);

        private string DescribeBucket(int bucket)
        {
            if (BucketStep == 1) return DescribePeriod(bucket);
            var (scr, met, allFuture, start, end) = ReadBucket(bucket);
            string heading = $"{start:00}:00 – {end:00}:00";
            if (allFuture) return heading + "\n未来时段";
            if (!_hasData && !HasCoverage) return heading + "\n" + _emptyText;

            double obs = 0;
            for (int i = start; i < end; i++) obs += Observed(i);
            bool missing = HasCoverage && obs <= 0.01 && met <= 0 && scr <= 0;
            string screenStr = missing ? "未记录" : MainWindow.FormatDuration(scr);
            string metricStr = missing ? "未记录" : (MetricIsLowerBound ? "≥ " : "") + MainWindow.FormatDuration(met);
            string values = ForegroundOnly ? $"{MetricLabel} {metricStr}" : $"亮屏 {screenStr}   ·   {MetricLabel} {metricStr}";
            string coverage = !HasCoverage ? "" : missing ? "\n未记录时段不计为零使用" : "\n已观测 " + MainWindow.FormatDuration(obs);

            var notes = new List<string>();
            if (_periodNotes != null)
            {
                for (int i = start; i < end && i < _periodNotes.Count; i++)
                    if (!string.IsNullOrEmpty(_periodNotes[i])) notes.Add(_periodNotes[i]);
            }
            string noteLine = notes.Count > 0 ? "\n" + notes[0] : "";
            return heading + "\n" + values + coverage + noteLine;
        }

        private string DescribePeriod(int index, bool includeNote = true)
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
            string note = includeNote && _periodNotes != null && index < _periodNotes.Count && !string.IsNullOrEmpty(_periodNotes[index])
                ? "\n" + _periodNotes[index]
                : "";
            return heading + "\n" + values + MetricLabel + " " + metric + coverage + note +
                (MetricIsLowerBound && !metricMissing ? "\n前台时长按旧记录可确认的下限显示" : "");
        }

        public void ShowDetails(string title)
        {
            var items = new List<ChartDetailItem>();
            if ((!_hasData && !HasCoverage) || PeriodCount == 0)
            {
                items.Add(new ChartDetailItem("暂无数据", _emptyText, "", 0, true, false));
            }
            else
            {
                double maxMetric = Math.Max(1, _foreground.DefaultIfEmpty(0).Max());
                for (int i = 0; i < PeriodCount; i++)
                {
                    string heading = PeriodCount == 24 ? PeriodLabel(i) + ":00 – " + (i + 1).ToString("00") + ":00" : PeriodLabel(i);
                    bool future = IsFuture(i);
                    double observed = Observed(i);
                    bool metricMissing = HasCoverage && observed <= .01 && At(_foreground, i) <= 0;
                    if (future)
                    {
                        items.Add(new ChartDetailItem(heading, "未来时段", "", 0, true, true));
                        continue;
                    }
                    var screenCoverage = _screenObserved ?? _observed;
                    bool screenMissing = screenCoverage.Length > 0 && At(screenCoverage, i) <= .01 && At(_screen, i) <= 0;
                    string screen = screenMissing ? "未记录" : MainWindow.FormatDuration(At(_screen, i));
                    string metric = metricMissing ? "未记录" : (MetricIsLowerBound ? "≥ " : "") + MainWindow.FormatDuration(At(_foreground, i));
                    string values = ForegroundOnly ? $"{MetricLabel} {metric}" : $"亮屏 {screen}   ·   {MetricLabel} {metric}";
                    string sub = !HasCoverage ? "" : metricMissing ? "未记录时段不计为零使用" :
                        (HasMissing(i) ? "部分记录 · 已观测 " : "已观测 ") + MainWindow.FormatDuration(observed);
                    if (_periodNotes != null && i < _periodNotes.Count && !string.IsNullOrEmpty(_periodNotes[i]))
                        sub = string.IsNullOrEmpty(sub) ? _periodNotes[i] : sub + " · " + _periodNotes[i];
                    double ratio = metricMissing ? 0 : Math.Clamp(At(_foreground, i) / maxMetric, 0, 1);
                    items.Add(new ChartDetailItem(heading, values, sub, ratio, metricMissing, false));
                }
            }
            var dialog = new ChartDetailsWindow(title, items.ToArray(), ThemeManager.IsDark);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if ((!_hasData && !HasCoverage) || PeriodCount == 0 || ActualWidth <= 8) return;
            double position = (e.GetPosition(this).X - 2) / (ActualWidth - 4);
            int vCount = VisualCount;
            int nextIndex = position < 0 || position >= 1 ? -1 : Math.Clamp((int)(position * vCount), 0, vCount - 1);
            if (nextIndex != _hoverIndex)
            {
                _hoverIndex = nextIndex;
                InvalidateVisual();
            }
            ToolTip = nextIndex < 0 ? null : DescribeBucket(nextIndex);
        }
    }
}
