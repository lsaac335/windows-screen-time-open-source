using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace ScreenTime;

public sealed class ActivityRing : FrameworkElement
{
    private double? _ratio;

    protected override AutomationPeer OnCreateAutomationPeer() => new ActivityRingPeer(this);

    private sealed class ActivityRingPeer : FrameworkElementAutomationPeer
    {
        public ActivityRingPeer(ActivityRing owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }

    public void SetUsage(double activeSeconds, double screenSeconds, bool hasData)
    {
        double? ratio = hasData && screenSeconds > 0 && double.IsFinite(screenSeconds) && double.IsFinite(activeSeconds)
            ? Math.Clamp(activeSeconds / screenSeconds, 0, 1)
            : null;
        if (ratio == _ratio) return;
        _ratio = ratio;
        AutomationProperties.SetName(this, "活跃时长占亮屏时长 " + ValueLabel);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        double thickness = 8;
        double radius = Math.Min(center.X, center.Y) - thickness;
        if (radius <= 0) return;

        drawing.DrawEllipse(ThemeManager.GetBrush("CardAltBackground"),
            new Pen(ThemeManager.GetBrush("ChartScreen"), thickness), center, radius, radius);
        if (_ratio is double ratio && ratio > 0)
        {
            var pen = new Pen(ThemeManager.GetBrush("ChartForeground"), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (ratio >= 1)
            {
                drawing.DrawEllipse(null, pen, center, radius, radius);
            }
            else
            {
                double angle = ratio * Math.PI * 2;
                var start = new Point(center.X, center.Y - radius);
                var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                var arc = new StreamGeometry();
                using (var context = arc.Open())
                {
                    context.BeginFigure(start, false, false);
                    context.ArcTo(end, new Size(radius, radius), 0, ratio > .5, SweepDirection.Clockwise, true, false);
                }
                drawing.DrawGeometry(null, pen, arc);
            }
        }

        bool compact = radius < 40;
        DrawLabel(drawing, ValueLabel, center.X, center.Y - (compact ? 16 : 24), compact ? 17 : 29, "AccentText", "Segoe UI", FontWeights.SemiBold);
        DrawLabel(drawing, "占亮屏时长", center.X, center.Y + (compact ? 4 : 10), compact ? 9 : 11, "Muted", "Microsoft YaHei UI", FontWeights.Normal);
    }

    private void DrawLabel(DrawingContext drawing, string value, double x, double y, double size, string brush, string font, FontWeight weight)
    {
        var label = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal),
            size, ThemeManager.GetBrush(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(label, new Point(x - label.Width / 2, y));
    }

    private string ValueLabel => _ratio.HasValue ? _ratio.Value.ToString("P0", CultureInfo.GetCultureInfo("zh-CN")) : "—";
}
