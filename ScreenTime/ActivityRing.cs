using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace ScreenTime;

public sealed class ActivityRing : FrameworkElement
{
    private static readonly Typeface ValueFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly CultureInfo ZhCn = CultureInfo.GetCultureInfo("zh-CN");

    private double? _ratio;
    private bool _cachedDark;
    private double _cachedThickness;
    private Pen? _trackPen;
    private Pen? _accentPen;

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

    private void EnsurePens(double thickness)
    {
        bool dark = ThemeManager.IsDark;
        if (_trackPen != null && _accentPen != null && _cachedDark == dark && Math.Abs(_cachedThickness - thickness) < 0.05)
            return;
        _cachedDark = dark;
        _cachedThickness = thickness;
        _trackPen = new Pen(ThemeManager.GetBrush("RingTrack"), thickness);
        _trackPen.Freeze();
        _accentPen = new Pen(ThemeManager.GetBrush("RingAccent"), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        _accentPen.Freeze();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double side = Math.Min(RenderSize.Width, RenderSize.Height);
        if (side <= 16) return;
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        double thickness = Math.Clamp(side * 0.092, 5.5, 8.5);
        double radius = side / 2 - thickness / 2 - 2;
        if (radius <= 4) return;

        EnsurePens(thickness);
        drawing.DrawEllipse(null, _trackPen, center, radius, radius);

        if (_ratio is double ratio && ratio > 0)
        {
            if (ratio >= 0.999)
            {
                drawing.DrawEllipse(null, _accentPen, center, radius, radius);
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
                arc.Freeze();
                drawing.DrawGeometry(null, _accentPen, arc);
            }
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double fontSize = Math.Clamp(side * 0.24, 14, 24);
        var valueText = new FormattedText(ValueLabel, ZhCn, FlowDirection.LeftToRight, ValueFace,
            fontSize, ThemeManager.GetBrush("AccentText"), dpi);
        drawing.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2));
    }

    private string ValueLabel => _ratio.HasValue ? _ratio.Value.ToString("P0", ZhCn) : "—";
}
