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
        string[] keys = { "WindowBackground", "CardBackground", "CardAltBackground", "Ink", "Muted", "Secondary", "Line", "Accent", "AccentSoft", "SidebarBackground", "SidebarActive", "SidebarMuted", "StatusGood", "StatusGoodBackground", "StatusWarning", "StatusWarningBackground", "StatusIdleBackground", "ChartGrid", "ChartAxis", "ChartScreen", "ChartForeground", "ChartMissing", "ChartFuture", "AccentText", "OnAccent", "InputBackground", "Danger" };
        string[] values = dark
            ? new[] { "#111620", "#1B2331", "#202A3B", "#EDF1FA", "#A1AEC5", "#7989A5", "#2D394E", "#A19BFF", "#302E52", "#0C111B", "#29334B", "#9AA8C2", "#6DD6B1", "#183A33", "#F3B77B", "#412E23", "#283144", "#2E394C", "#8A9BB6", "#454567", "#9690F7", "#202A3C", "#151C29", "#D8D4FF", "#FFFFFF", "#151D2A", "#F19393" }
            : new[] { "#F5F7FC", "#FFFFFF", "#EDF1F8", "#19233E", "#65738A", "#8A96AC", "#E1E6F0", "#625BE7", "#EFEEFF", "#19233E", "#303B5B", "#97A3BD", "#258561", "#E9F6EF", "#AD633A", "#FFF0E7", "#EBEEF5", "#E8ECF4", "#8D99AE", "#D5D3F7", "#716AEA", "#F0F2F8", "#FAFBFE", "#625BE7", "#FFFFFF", "#F9FAFE", "#BC5757" };
        var result = new Dictionary<string, Brush>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Length; i++) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i])); brush.Freeze(); result[keys[i]] = brush; }
        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#665DE0" : "#625BE7")); fill.Freeze(); result["AccentFill"] = fill;
        var sidebarCard = new SolidColorBrush(Color.FromRgb(28, 38, 59)); sidebarCard.Freeze(); result["SidebarCardBackground"] = sidebarCard;
        var captionHover = new SolidColorBrush(dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0)); captionHover.Freeze(); result["CaptionHover"] = captionHover;
        var captionPressed = new SolidColorBrush(dark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0)); captionPressed.Freeze(); result["CaptionPressed"] = captionPressed;
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
        if (parent is UsageChart chart) chart.InvalidateVisual();
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
