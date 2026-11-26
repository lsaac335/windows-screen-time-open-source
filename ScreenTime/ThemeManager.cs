using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenTime;

public static class ThemeManager
{
    private sealed class WindowTheme { public bool? Dark; public bool Subscribed; }
    private static readonly ConditionalWeakTable<Window, WindowTheme> States = new();
    public static bool IsDark { get; private set; } = true;
    private static readonly Dictionary<string, Brush> Dark = Build(true);
    private static readonly Dictionary<string, Brush> Light = Build(false);

    public static Brush GetBrush(string key) => (IsDark ? Dark : Light).TryGetValue(key, out var brush) ? brush : Brushes.Transparent;
    private static Dictionary<string, Brush> Build(bool dark)
    {
        var result = new Dictionary<string, Brush>(StringComparer.Ordinal);
        static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
        void Add(string key, Brush brush)
        {
            brush.Freeze();
            result.Add(key, brush);
        }
        void Solid(string key, string night, string day) => Add(key, new SolidColorBrush(Parse(dark ? night : day)));
        LinearGradientBrush Linear(string[] night, string[] day, bool vertical = false)
        {
            var colors = dark ? night : day;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(vertical ? 0 : 1, 1) };
            for (int index = 0; index < colors.Length; index++)
                brush.GradientStops.Add(new GradientStop(Parse(colors[index]), (double)index / (colors.Length - 1)));
            return brush;
        }
        RadialGradientBrush Glow(Point center, double radiusX, double radiusY, string night, string day)
        {
            var tint = Parse(dark ? night : day);
            var fade = Color.FromArgb(0, tint.R, tint.G, tint.B);
            return new RadialGradientBrush(tint, fade)
            {
                Center = center, GradientOrigin = center, RadiusX = radiusX, RadiusY = radiusY
            };
        }
        void Surface(string key, Action<DrawingContext, Rect> draw)
        {
            var unit = new Rect(0, 0, 1, 1);
            var layers = new DrawingGroup { ClipGeometry = new RectangleGeometry(unit) };
            using (var drawing = layers.Open()) draw(drawing, unit);
            Add(key, new DrawingBrush(layers)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = unit, Stretch = Stretch.Fill
            });
        }
        Solid("WindowBackground", "#262339", "#E9E2F6");
        Solid("CardBackground", "#282439", "#FCFAFF");
        Solid("CardAltBackground", "#29283E", "#F5F3FA");
        Solid("Ink", "#F5F0FF", "#26243E");
        Solid("Muted", "#C9BEDC", "#59536D");
        Solid("Secondary", "#C7BADB", "#645D76");
        Solid("Line", "#544764", "#E4DDEF");
        Solid("Accent", "#D3BAFF", "#6241A7");
        Solid("AccentFill", "#7954BB", "#744DBA");
        Solid("AccentSoft", "#47365D", "#EFE7FC");
        Solid("AccentText", "#E4D7FC", "#53368F");
        Solid("FocusStroke", "#F4DFFF", "#593699");
        Solid("ToggleTrack", "#786993", "#837396");
        Solid("OnAccent", "#FFFFFF", "#FFFFFF");
        Solid("SidebarMuted", "#D7CDE8", "#554D69");
        Solid("GlassDepth", "#54101024", "#21664D90");
        Solid("StatusGood", "#80DCAD", "#227553");
        Solid("StatusGoodBackground", "#253E38", "#E7F5ED");
        Solid("StatusWarning", "#F0B987", "#945130");
        Solid("StatusWarningBackground", "#48342E", "#FFF0E7");
        Solid("StatusIdleBackground", "#393348", "#F2EDF8");
        Solid("ChartGrid", "#453D58", "#EAE4F4");
        Solid("ChartAxis", "#B8ADC9", "#6A627D");
        Solid("ChartScreen", "#5B4B79", "#D9CBF5");
        Solid("ChartScreenStroke", "#C1B6D8", "#705B8C");
        Solid("ChartForeground", "#B597FF", "#7655CC");
        Solid("ChartMissing", "#343047", "#F2EDF8");
        Solid("ChartHatch", "#5B526F", "#D1C4E2");
        Solid("ChartFuture", "#332D46", "#F1EDF8");
        Solid("InputBackground", "#302A42", "#FDFBFF");
        Solid("Danger", "#F6A7AC", "#9F404C");
        Solid("CaptionHover", "#16FFFFFF", "#10000000");
        Solid("CaptionPressed", "#24FFFFFF", "#1A000000");
        Add("GlassEdge", Linear(new[] { "#D6DCC6FF", "#597C6AA0", "#A0ADDCF1" },
                                new[] { "#FFFFFFFF", "#ADE6D9F6", "#F2FFFFFF" }));
        Add("GlassInset", Linear(new[] { "#44EDDBFF", "#00DFD2FA", "#379FE1E8" },
                                 new[] { "#A2FFFFFF", "#00FFFFFF", "#82F8FFFF" }));
        Add("GlassControl", Linear(new[] { "#B45E4D76", "#CD362D4B", "#B644425E" },
                                   new[] { "#EFFFFFFF", "#BAF2E8FC", "#D4F9FCFF" }, vertical: true));
        Add("SidebarActive", Linear(new[] { "#CF594675", "#D741385A", "#AD574C78" },
                                    new[] { "#F8FFFFFF", "#DBEEE2FB", "#EAF9F4FF" }, vertical: true));
        Add("GlassSelection", Linear(new[] { "#FF58436E", "#FF473557" },
                                     new[] { "#FFF9F2FF", "#FFEBDEF9" }, vertical: true));
        Surface("GlassBackdrop", (drawing, unit) =>
        {
            drawing.DrawRectangle(Linear(new[] { "#3F2E56", "#28233E", "#1E2A41", "#203847" },
                                         new[] { "#DFCBF4", "#EFE6FA", "#E9EFFC", "#CDEDE8" }), null, unit);
            drawing.DrawRectangle(Glow(new Point(.02, .23), .56, .70, "#858152AF", "#8ACFA7F5"), null, unit);
            drawing.DrawRectangle(Glow(new Point(.91, .04), .65, .65, "#445C6BC2", "#80BFCCFF"), null, unit);
            drawing.DrawRectangle(Glow(new Point(1, .96), .77, .61, "#673B9CA5", "#92ABDDD5"), null, unit);
            var reflection = Glow(new Point(.53, .51), .65, .18, "#327C9AD8", "#5EFFFFFF");
            reflection.RelativeTransform = new RotateTransform(-27, .53, .51);
            drawing.DrawRectangle(reflection, null, unit);
            var sweep = Geometry.Parse("M -0.1,0.38 C 0.12,0.64 0.35,0.64 0.57,0.38 C 0.75,0.16 0.85,0.11 1.1,0.27 L 1.1,0.62 C 0.80,0.39 0.71,0.62 0.49,0.78 C 0.23,0.98 0.06,0.92 -0.1,0.77 Z");
            drawing.DrawGeometry(Linear(new[] { "#168BCDE8", "#25A589EF", "#156FC7D2" },
                                        new[] { "#22FFFAFF", "#50F7F0FF", "#37B7E3EB" }), null, sweep);
        });
        Surface("MainWindowEdgeDepth", (drawing, unit) =>
        {
            void FadeInward(Point start, Point end, string night, string day)
            {
                var tint = Parse(dark ? night : day);
                var fade = new LinearGradientBrush(tint, Color.FromArgb(0, tint.R, tint.G, tint.B), start, end);
                fade.GradientStops.Insert(1, new GradientStop(Color.FromArgb((byte)(tint.A * .42), tint.R, tint.G, tint.B), .35));
                drawing.DrawRectangle(fade, null, unit);
            }

            drawing.DrawRectangle(Glow(new Point(.04, .012), .34, .22, "#31D6CCFF", "#37FFFBFF"), null, unit);
            drawing.DrawRectangle(Glow(new Point(.98, .98), .29, .19, "#3A9EF5EA", "#32F4FFFF"), null, unit);
            FadeInward(new Point(0, 0), new Point(.054, 0), "#440C0A1F", "#167E61A4");
            FadeInward(new Point(1, 0), new Point(.947, 0), "#530A1727", "#20607C9E");
            FadeInward(new Point(0, 1), new Point(0, .875), "#68070C1B", "#25735D96");
            FadeInward(new Point(0, 0), new Point(0, .056), "#629F8BCC", "#49FFFAFF");
        });
        Surface("SidebarBackground", (drawing, unit) =>
        {
            drawing.DrawRectangle(Linear(new[] { "#C747375F", "#9E342841", "#B1363953" },
                                         new[] { "#94FFFFFF", "#7BF9F1FF", "#C2F8FCFF" }), null, unit);
            drawing.DrawRectangle(Glow(new Point(.14, .0), 1.25, .48, "#41E0BAFF", "#CBFFFFFF"), null, unit);
            drawing.DrawRectangle(Glow(new Point(1.0, .99), 1.10, .37, "#2195E9EE", "#63E2F9FA"), null, unit);
        });
        Surface("GlassHero", (drawing, unit) =>
        {
            drawing.DrawRectangle(Linear(new[] { "#EA443453", "#E2332948", "#CA2C324E" },
                                         new[] { "#EFFFFFFF", "#D4F7EEFF", "#A8EAF2FC" }), null, unit);
            drawing.DrawRectangle(Glow(new Point(.10, 0), .77, .95, "#31E8BDFF", "#CEFFFFFF"), null, unit);
            drawing.DrawRectangle(Glow(new Point(.90, .03), .60, 1.18, "#2A879DEC", "#5BCDD8F9"), null, unit);
        });
        Surface("GlassWidget", (drawing, unit) =>
        {
            // Keep the base tint sufficiently dense against a light or dark wallpaper.
            drawing.DrawRectangle(Linear(new[] { "#F647365E", "#F3322948", "#F1283D50" },
                                         new[] { "#FAF7E9FF", "#F9F2E8FC", "#F8E7F8F8" }), null, unit);
            drawing.DrawRectangle(Glow(new Point(.00, .03), .98, .66, "#39E5BEFF", "#DAFFFFFF"), null, unit);
            drawing.DrawRectangle(Glow(new Point(.98, 1), .93, .62, "#3495DAE0", "#66C0F0E8"), null, unit);
        });
        return result;
    }

    public static void Apply(Window window, bool dark)
    {
        IsDark = dark;
        var state = States.GetValue(window, _ => new WindowTheme());
        if (state.Dark == dark) return;
        state.Dark = dark;
        foreach (var entry in dark ? Dark : Light) window.Resources[entry.Key] = entry.Value;
        var colors = dark ? Dark : Light;
        window.Resources[SystemColors.WindowBrushKey] = colors["CardBackground"];
        window.Resources[SystemColors.WindowTextBrushKey] = colors["Ink"];
        window.Resources[SystemColors.ControlBrushKey] = colors["InputBackground"];
        window.Resources[SystemColors.ControlTextBrushKey] = colors["Ink"];
        window.Resources[SystemColors.HighlightBrushKey] = colors["AccentFill"];
        if (!window.AllowsTransparency)
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        window.SetResourceReference(Window.ForegroundProperty, "Ink");
        if (!state.Subscribed)
        {
            state.Subscribed = true;
            window.SourceInitialized += (_, _) => ApplyCaption(window, state.Dark ?? true);
        }
        ApplyCaption(window, dark);
        Invalidate(window);
    }
    private static void Invalidate(DependencyObject parent)
    {
        // Detail views also contain palette brushes assigned in code. Update those
        // without rebuilding the dialog or replacing non-theme artwork/brushes.
        List<(DependencyProperty Property, Brush Value)>? changes = null;
        var values = parent.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var entry = values.Current;
            if (entry.Value is not Brush current || entry.Property.ReadOnly) continue;
            foreach (string key in Dark.Keys)
                if (ReferenceEquals(current, Dark[key]) || ReferenceEquals(current, Light[key]))
                {
                    Brush target = (IsDark ? Dark : Light)[key];
                    if (!ReferenceEquals(current, target)) (changes ??= new()).Add((entry.Property, target));
                    break;
                }
        }
        if (changes != null) foreach (var change in changes) parent.SetValue(change.Property, change.Value);
        if (parent is UsageChart or ActivityRing) ((UIElement)parent).InvalidateVisual();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Invalidate(VisualTreeHelper.GetChild(parent, i));
    }
    private static void ApplyCaption(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            int flag = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref flag, sizeof(int));
            var palette = dark ? Dark : Light;
            int ColorRef(string key) { var c = ((SolidColorBrush)palette[key]).Color; return c.R | c.G << 8 | c.B << 16; }
            int caption = ColorRef("WindowBackground"), text = ColorRef("Ink");
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static Brush LegacyBrush(string color)
    {
        string key = color.ToUpperInvariant() switch
        {
            "#F7F8FC" => "WindowBackground", "#FFFFFF" => "CardBackground", "#F0F1F7" => "Line",
            "#5854E8" => "Accent", "#EFEEFF" => "AccentSoft", "#DAD7FD" => "Line", "#E5E8F0" => "Line",
            "#19233E" => "SidebarBackground", "#303B5B" => "SidebarActive", "#8E99B3" => "SidebarMuted",
            "#C56E45" => "StatusWarning", "#318765" => "StatusGood", "#8B91A3" => "Secondary",
            "#FFF0E9" => "StatusWarningBackground", "#EDEFF4" => "StatusIdleBackground", "#EAF6F0" => "StatusGoodBackground",
            "#8E97AA" or "#929AAF" or "#818BA1" or "#717C93" => "Muted",
            "#A8AFBF" or "#9CA4B6" or "#939BB0" or "#C8CAE7" => "Secondary",
            _ => ""
        };
        if (key.Length > 0) return GetBrush(key);
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
}
