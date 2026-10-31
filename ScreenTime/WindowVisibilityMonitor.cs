using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTime;

/// <summary>Runs on the existing sampler STA. No screenshots, titles, input hooks or worker threads.</summary>
internal sealed class WindowVisibilityMonitor : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly List<IntPtr> _hooks = new();
    private IVirtualDesktopManager? _desktops;
    private bool _dirty = true, _enabled, _reading;
    private long _lastRead;
    public VisibleApp[] Apps { get; private set; } = Array.Empty<VisibleApp>();
    public bool Available { get; private set; }
    public int Samples { get; private set; }
    public string Status { get; private set; } = "off";

    public WindowVisibilityMonitor()
    {
        _callback = (_, type, hwnd, objectId, childId, _, _) =>
        {
            // Ignore child controls, text/caret changes, and high-frequency cursor motion.
            if ((type < 0x8000 || objectId == 0) && hwnd != IntPtr.Zero) _dirty = true;
        };
        Hook(0x8000, 0x8003); // create/destroy/show/hide
        Hook(0x8004, 0x8004); // reorder
        Hook(0x800B, 0x800B); // window rectangle
        Hook(0x0016, 0x0017); // minimize start/end
        try { _desktops = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!; }
        catch (COMException) { }
    }
    private void Hook(uint first, uint last)
    {
        var hook = NativeMethods.SetWinEventHook(first, last, IntPtr.Zero, _callback, 0, 0, 0);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }
    public void Update(bool enabled, Func<IntPtr, VisibleApp?> resolve)
    {
        if (!enabled)
        { _enabled = false; Available = false; Apps = Array.Empty<VisibleApp>(); Status = "off"; return; }
        long now = Stopwatch.GetTimestamp();
        double age = (now - _lastRead) / (double)Stopwatch.Frequency;
        // Coalesce event storms, with a three-second periodic reconciliation. No extra timer.
        if (_reading || (_enabled && age < (_dirty ? 1 : 3))) return;
        _enabled = true; _dirty = false; _lastRead = now; _reading = true;
        try
        {
            if (_desktops == null)
            { Available = false; Apps = Array.Empty<VisibleApp>(); Status = "virtual-desktop-unavailable"; return; }
            var monitors = new List<WindowRect>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Rect r, IntPtr p) =>
            { monitors.Add(r.Value); return true; }, IntPtr.Zero);
            if (monitors.Count == 0) throw new InvalidOperationException("no-display-bounds");
            var geometry = new VisibilityGeometry(monitors);
            var apps = new Dictionary<string, VisibleApp>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            Exception? callbackError = null;
            bool complete = EnumWindows((hwnd, data) =>
            {
                try
                {
                    if (++count > 4096) return false;
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                    if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0) return true;
                    if (_desktops.IsWindowOnCurrentVirtualDesktop(hwnd, out bool current) < 0 || !current) return true;
                    if (DwmGetBounds(hwnd, 9, out var bounds, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out bounds)) return true;
                    var rect = bounds.Value;
                    if (rect.Area == 0) return true;
                    var className = new StringBuilder(128); NativeMethods.GetClassName(hwnd, className, className.Capacity);
                    string cls = className.ToString();
                    // Desktop surfaces and taskbars have no app exposure attribution.
                    if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                    double area = geometry.VisibleArea(rect);
                    double onScreen = monitors.Sum(m => (double)m.Intersect(rect).Area);
                    long extended = GetWindowLongPtr(hwnd, -20).ToInt64();
                    bool utilityOverlay = (extended & (0x20L | 0x08000000L)) != 0; // click-through / no-activate
                    NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (!utilityOverlay && pid != Environment.ProcessId && area >= 4096 && area >= onScreen * .1)
                    {
                        var app = resolve(hwnd);
                        if (app != null && app.Id != UsageRules.SelfAppId) apps[app.Id] = app;
                    }
                    bool opaque = (extended & 0x80000) == 0; // WS_EX_LAYERED
                    if (!opaque && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags))
                        opaque = (flags & 1) == 0 && (flags & 2) != 0 && alpha == 255;
                    // Alpha-composited windows have no reliable rectangular opaque footprint.
                    // Do not let transparent widget margins hide other applications.
                    if (opaque) geometry.Cover(rect);
                    return geometry.IsReliable;
                }
                catch (Exception ex) { callbackError = ex; return false; }
            }, IntPtr.Zero);
            if (!complete || !geometry.IsReliable || callbackError != null) throw new InvalidOperationException(callbackError?.Message ?? "visibility-budget-or-enumeration");
            Apps = apps.Values.ToArray(); Available = true; Samples++; Status = "ready:" + Apps.Length;
        }
        catch (Exception ex) { Available = false; Apps = Array.Empty<VisibleApp>(); Status = "unavailable:" + ex.Message; }
        finally { _reading = false; }
    }
    public void Dispose()
    {
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        if (_desktops != null) Marshal.FinalReleaseComObject(_desktops);
        _desktops = null; Apps = Array.Empty<VisibleApp>();
    }
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid id);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; public WindowRect Value => new(Left, Top, Right, Bottom); }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(NativeMethods.EnumWindowsDelegate callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint color, out byte alpha, out uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetBounds(IntPtr hwnd, int attribute, out Rect value, int size);
}
