using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenTime;

public partial class MainWindow
{
    private static readonly Geometry MaximizeCaptionGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z").GetAsFrozen() as Geometry ?? Geometry.Empty;
    private static readonly Geometry RestoreCaptionGeometry = Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z").GetAsFrozen() as Geometry ?? Geometry.Empty;
    private void UpdateCaptionLayout()
    {
        if (DockMaximizeButton == null || WindowFrame == null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = maximized ? RestoreCaptionGeometry : MaximizeCaptionGeometry;
        DockMaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(DockMaximizeButton, maximized ? "还原" : "最大化");
        var margin = new Thickness(0);
        if (maximized)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = new CaptionMonitorInfo { Size = Marshal.SizeOf<CaptionMonitorInfo>() };
            if (GetWindowRect(handle, out var rect) && GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
            {
                // Only inset the invisible frame that actually extends beyond this monitor's work area.
                // WindowChrome and per-monitor DPI can produce different frame sizes; never assume 7 DIP.
                var dpi = VisualTreeHelper.GetDpi(this);
                margin = new Thickness(Math.Max(0, monitor.Work.Left - rect.Left) / dpi.DpiScaleX,
                    Math.Max(0, monitor.Work.Top - rect.Top) / dpi.DpiScaleY,
                    Math.Max(0, rect.Right - monitor.Work.Right) / dpi.DpiScaleX,
                    Math.Max(0, rect.Bottom - monitor.Work.Bottom) / dpi.DpiScaleY);
            }
        }
        if (WindowFrame.Margin != margin) WindowFrame.Margin = margin;
    }

    [StructLayout(LayoutKind.Sequential)] private struct CaptionRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CaptionMonitorInfo { public int Size; public CaptionRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out CaptionRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref CaptionMonitorInfo info);
}
