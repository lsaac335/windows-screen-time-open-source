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
        Solid("WindowBackground", "#0B0D14", "#F2F4F8");
        Solid("CardBackground", "#131722", "#FFFFFF");
        Solid("CardAltBackground", "#191E2D", "#F5F7FC");
        Solid("Ink", "#F3F5F9", "#111827");
        Solid("Muted", "#8E97AA", "#64748B");
        Solid("Secondary", "#667085", "#94A3B8");
        Solid("Line", "#202636", "#E6EAF2");
        Solid("Accent", "#7C8AFF", "#4F5CE0");
        Solid("AccentFill", "#5B6AF0", "#4F5CE0");
        Solid("AccentSoft", "#1D2340", "#EEF0FE");
        Solid("AccentText", "#B8C2FF", "#3F4CC7");
        Solid("FocusStroke", "#8F9BFF", "#4F5CE0");
        Solid("ToggleTrack", "#2F374C", "#CBD5E1");
        Solid("OnAccent", "#FFFFFF", "#FFFFFF");
        Solid("SidebarMuted", "#8E97AA", "#475569");
        Solid("GlassDepth", "#6004060C", "#120F172A");
        Solid("StatusGood", "#34D399", "#059669");
        Solid("StatusGoodBackground", "#112923", "#E6F7F0");
        Solid("StatusWarning", "#FBBF24", "#B45309");
        Solid("StatusWarningBackground", "#2B2212", "#FEF3DC");
        Solid("StatusIdleBackground", "#1B2030", "#EEF1F6");
        Solid("ChartGrid", "#1B2130", "#EEF1F6");
        Solid("ChartAxis", "#757F94", "#8A94A6");
        Solid("ChartScreen", "#252E4A", "#DCE1F8");
        Solid("ChartScreenStroke", "#16FFFFFF", "#124F5CE0");
        Solid("ChartForeground", "#7C8AFF", "#4F5CE0");
        Solid("ChartMissing", "#06FFFFFF", "#050F172A");
        Solid("ChartHatch", "#10FFFFFF", "#100F172A");
        Solid("ChartFuture", "#05FFFFFF", "#040F172A");
        Solid("InputBackground", "#0F121B", "#FFFFFF");
        Solid("Danger", "#F87171", "#DC2626");
        Solid("CaptionHover", "#14FFFFFF", "#0C0F172A");
        Solid("CaptionPressed", "#20FFFFFF", "#140F172A");
        Solid("HoverOverlay", "#10FFFFFF", "#080F172A");
        Solid("PressedOverlay", "#1E000000", "#100F172A");
        Solid("RowHover", "#10FFFFFF", "#80EEF0FE");
        Solid("ProgressTrack", "#16FFFFFF", "#E8ECF4");
        Solid("ChartScreenFill", "#367C8AFF", "#2B4F5CE0");
        Solid("ChartHoverBand", "#147C8AFF", "#124F5CE0");
        Solid("RingTrack", "#232A3E", "#E6EAF3");
        Add("ChartForegroundBrush", Linear(new[] { "#7C8AFF", "#46C6E8" },
                                           new[] { "#4F5CE0", "#0EA5E9" }, vertical: true));
        Add("ProgressBarBrush", Linear(new[] { "#7C8AFF", "#46C6E8" },
                                       new[] { "#4F5CE0", "#0EA5E9" }, vertical: false));
        Add("RingAccent", Linear(new[] { "#7C8AFF", "#5A9BF6", "#46C6E8" },
                                 new[] { "#4F5CE0", "#2B85E8", "#0EA5E9" }));
        Solid("GlassCardEdge", "#202636", "#E6EAF2");
        Solid("GlassEdge", "#2A3248", "#E0E5F0");
        Solid("GlassInset", "#12FFFFFF", "#66FFFFFF");
        Solid("GlassControl", "#171C2B", "#FFFFFF");
        Solid("SidebarActive", "#1D2340", "#EEF0FE");
        Solid("GlassSelection", "#212848", "#EEF0FE");
        Solid("GlassCard", "#131722", "#FFFFFF");
        Add("GlassBackdrop", Linear(new[] { "#0B0D14", "#0E111B" },
                                    new[] { "#F3F5F8", "#EDF0F5" }, vertical: true));
        Solid("MainWindowEdgeDepth", "#00000000", "#00000000");
        Solid("SidebarBackground", "#111520", "#FFFFFF");
        Add("GlassHero", Linear(new[] { "#181E30", "#131724" },
                                new[] { "#FFFFFF", "#F5F7FF" }));
        Solid("GlassWidget", "#111520", "#FFFFFF");
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
