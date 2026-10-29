using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenTime;

/// <summary>
/// Best-effort desktop parenting for this process's widget only. Explorer's window layout is
/// not a public contract, so an unsupported desktop must fall back to an ordinary widget.
/// No Explorer window is created, hidden, resized, reordered, or otherwise modified here.
/// </summary>
public static class DesktopWidgetHost
{
    private const int StyleIndex = -16, OwnerIndex = -8;
    private const long ChildStyle = 0x40000000L;
    private const long PopupStyle = 0x80000000L;
    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010, FrameChanged = 0x0020;
    private static readonly ConditionalWeakTable<Window, Attachment> Attachments = new();
    private static bool _desktopAttachmentDisabled;

    private sealed class Attachment
    {
        public IntPtr Handle;
        public IntPtr Host;
        public IntPtr OriginalOwner;
        public long OriginalStyle;
        public bool Recovering;
    }

    /// <summary>
    /// Call on the window's dispatcher after showing/positioning it. The widget must be an
    /// unowned, non-topmost window with ShowInTaskbar=false and ShowActivated=false.
    /// A false result leaves it as an ordinary top-level window, with its position preserved.
    /// </summary>
    public static bool TryAttach(Window window, out string reason)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        window.Dispatcher.VerifyAccess();
        reason = "";
        if (IsAttached(window)) return true;
        if (Attachments.TryGetValue(window, out _)) Detach(window);
        if (_desktopAttachmentDisabled)
        {
            reason = "本次运行已停用不兼容的桌面附着，组件使用普通浮窗。";
            return false;
        }
        if (window.Owner != null)
        {
            reason = "组件仍有窗口所有者，已使用普通组件窗口。";
            return false;
        }
        if (window.Topmost)
        {
            reason = "悬浮置顶模式不附着到桌面。";
            return false;
        }

        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        if (!IsWindow(handle) || !GetWindowRect(handle, out var screen))
        {
            reason = "组件窗口已失效，需要重新创建。";
            return false;
        }
        GetWindowThreadProcessId(handle, out uint ownerProcess);
        if (ownerProcess != Environment.ProcessId)
        {
            reason = "组件窗口不属于本程序，已取消桌面附着。";
            return false;
        }
        var state = new Attachment
        {
            Handle = handle, OriginalStyle = GetWindowLongPtr(handle, StyleIndex).ToInt64(),
            OriginalOwner = GetWindowLongPtr(handle, OwnerIndex)
        };
        if ((state.OriginalStyle & ChildStyle) != 0)
        {
            reason = "组件已有父窗口，已保留当前窗口。";
            return false;
        }
        // WPF implements ShowInTaskbar=false with its own invisible owner HWND. It is not
        // Window.Owner, and must be restored natively without assigning the managed Owner
        // property (which would ask WPF to create/reparent that hidden window again).
        if (state.OriginalOwner != IntPtr.Zero && !IsImplicitWpfOwner(state.OriginalOwner))
        {
            reason = "组件已有自定义窗口所有者，已保留普通组件窗口。";
            return false;
        }

        bool registered = false;

        try
        {
            state.Host = FindDesktopHost();
            if (state.Host == IntPtr.Zero)
            {
                reason = "当前桌面未提供可用的组件层，已使用普通组件窗口。";
                return false;
            }
            // A cross-process SetParent with mismatched DPI contexts can reset the child
            // process's DPI awareness. Reject it before changing either style or parent.
            if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle), GetWindowDpiAwarenessContext(state.Host)))
            {
                reason = "桌面与组件的缩放模式不兼容，已使用普通组件窗口。";
                return false;
            }

            // Keep recovery information before the first native change, not after SetWindowPos.
            Attachments.Add(window, state);
            registered = true;
            if (state.OriginalOwner != IntPtr.Zero) SetOwnLong(handle, OwnerIndex, IntPtr.Zero);
            SetOwnStyle(handle, (state.OriginalStyle & ~PopupStyle) | ChildStyle);
            NativeSetLastError(0);
            IntPtr previous = SetParent(handle, state.Host);
            int error = Marshal.GetLastWin32Error();
            if ((previous == IntPtr.Zero && error != 0) || GetParent(handle) != state.Host)
                throw new Win32Exception(error == 0 ? 1400 : error);

            var point = new NativePoint { X = screen.Left, Y = screen.Top };
            MapWindowPoints(IntPtr.Zero, state.Host, ref point, 1);
            if (!SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, screen.Right - screen.Left,
                    screen.Bottom - screen.Top, NoActivate | FrameChanged))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // Synchronize this child's focus cues after changing its parent.
            SendMessage(handle, 0x0127, new IntPtr(3), IntPtr.Zero); // WM_CHANGEUISTATE / UIS_INITIALIZE
            return true;
        }
        catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            if (registered)
            {
                if (!TryRestoreTopLevel(state, screen, out var recoveryError))
                    AbortBrokenAttachment(window, state, recoveryError!);
                Attachments.Remove(window);
            }
            reason = error is EntryPointNotFoundException
                ? "当前 Windows 不支持所需的桌面缩放接口，已使用普通组件窗口。"
                : "桌面组件层连接失败，已恢复普通组件窗口。";
            return false;
        }
    }

    /// <summary>Restore only this widget to a top-level window, retaining its current screen position.</summary>
    public static void Detach(Window window)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        window.Dispatcher.VerifyAccess();
        if (!Attachments.TryGetValue(window, out var state)) return;
        if (state.Recovering) return;
        if (!IsWindow(state.Handle)) { Attachments.Remove(window); return; }
        if (!GetWindowRect(state.Handle, out var screen))
            AbortBrokenAttachment(window, state, new Win32Exception(Marshal.GetLastWin32Error()));
        if (!TryRestoreTopLevel(state, screen, out var error))
            AbortBrokenAttachment(window, state, error!);
        Attachments.Remove(window);
    }

    /// <summary>
    /// False also detects an Explorer restart. A destroyed widget HWND must be recreated by
    /// the owner; the hidden main window/controller should remain alive independently.
    /// </summary>
    public static bool IsAttached(Window window)
    {
        if (window == null) return false;
        return Attachments.TryGetValue(window, out var state) && IsWindow(state.Handle) &&
            IsWindow(state.Host) && GetParent(state.Handle) == state.Host &&
            (GetWindowLongPtr(state.Handle, StyleIndex).ToInt64() & ChildStyle) != 0;
    }

    /// <summary>
    /// Return screen coordinates in the widget's current DPI units. Use the matching MoveToScreen
    /// method while dragging: native child positions are parent-relative, whereas these are not.
    /// </summary>
    public static (double Left, double Top) GetScreenPosition(Window window)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        window.Dispatcher.VerifyAccess();
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var screen))
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            return (screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY);
        }
        return (double.IsFinite(window.Left) ? window.Left : 0, double.IsFinite(window.Top) ? window.Top : 0);
    }

    public static void MoveToScreen(Window window, double leftDip, double topDip)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        window.Dispatcher.VerifyAccess();
        if (!double.IsFinite(leftDip) || !double.IsFinite(topDip)) throw new ArgumentOutOfRangeException(nameof(leftDip));
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(window);
        var point = new NativePoint { X = (int)Math.Round(leftDip * dpi.DpiScaleX), Y = (int)Math.Round(topDip * dpi.DpiScaleY) };
        long style = GetWindowLongPtr(handle, StyleIndex).ToInt64();
        if ((style & ChildStyle) != 0)
        {
            IntPtr parent = GetParent(handle);
            if (parent != IntPtr.Zero) MapWindowPoints(IntPtr.Zero, parent, ref point, 1);
        }
        if (!SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, NoSize | NoZOrder | NoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static IntPtr FindDesktopHost()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(shell, out uint shellProcess);
        IntPtr result = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out uint candidateProcess);
            if (candidateProcess != shellProcess || !IsWindowVisible(candidate)) return true;
            var name = new StringBuilder(64);
            GetClassName(candidate, name, name.Capacity);
            string className = name.ToString();
            if (className is not ("Progman" or "WorkerW")) return true;
            if (FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            result = candidate;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static void SetOwnStyle(IntPtr handle, long style)
    {
        SetOwnLong(handle, StyleIndex, new IntPtr(style));
    }

    private static void SetOwnLong(IntPtr handle, int index, IntPtr value)
    {
        NativeSetLastError(0);
        IntPtr previous = SetWindowLongPtr(handle, index, value);
        int error = Marshal.GetLastWin32Error();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
    }

    private static bool IsImplicitWpfOwner(IntPtr owner)
    {
        if (!IsWindow(owner) || IsWindowVisible(owner)) return false;
        GetWindowThreadProcessId(owner, out uint process);
        if (process != Environment.ProcessId) return false;
        var name = new StringBuilder(256);
        var title = new StringBuilder(64);
        GetClassName(owner, name, name.Capacity);
        GetWindowText(owner, title, title.Capacity);
        return name.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal) && title.ToString() == "Hidden Window";
    }

    private static bool TryRestoreTopLevel(Attachment state, NativeRect screen, out Exception? error)
    {
        error = null;
        if (!IsWindow(state.Handle)) return true;
        try
        {
            long current = GetWindowLongPtr(state.Handle, StyleIndex).ToInt64();
            // Reparent first while WS_CHILD is still set. On failure never clear that bit and
            // pretend the window has become a top-level window.
            if ((current & ChildStyle) != 0 || GetParent(state.Handle) == state.Host)
            {
                NativeSetLastError(0);
                IntPtr previous = SetParent(state.Handle, IntPtr.Zero);
                int parentError = Marshal.GetLastWin32Error();
                IntPtr parent = GetParent(state.Handle);
                // Until WS_CHILD is cleared, NULL reparenting can expose the desktop HWND
                // as the new parent. Both desktop and zero are valid here.
                if ((previous == IntPtr.Zero && parentError != 0) ||
                    (parent != IntPtr.Zero && parent != GetDesktopWindow()))
                    throw new Win32Exception(parentError == 0 ? 1400 : parentError);
            }
            // Restore only parenting bits, retaining visibility and later WPF style changes.
            current = GetWindowLongPtr(state.Handle, StyleIndex).ToInt64();
            SetOwnStyle(state.Handle, (current & ~(ChildStyle | PopupStyle)) |
                (state.OriginalStyle & (ChildStyle | PopupStyle)));
            if (state.OriginalOwner != IntPtr.Zero && !IsWindow(state.OriginalOwner))
                throw new Win32Exception(1400);
            SetOwnLong(state.Handle, OwnerIndex, state.OriginalOwner);
            if (GetWindowLongPtr(state.Handle, OwnerIndex) != state.OriginalOwner ||
                (GetWindowLongPtr(state.Handle, StyleIndex).ToInt64() & ChildStyle) != 0)
                throw new Win32Exception(1400);
            if (!SetWindowPos(state.Handle, IntPtr.Zero, screen.Left, screen.Top, Math.Max(1, screen.Right - screen.Left),
                    Math.Max(1, screen.Bottom - screen.Top), NoZOrder | NoActivate | FrameChanged))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        }
        catch (Exception failure) when (failure is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            error = failure;
            return false;
        }
    }

    private static void AbortBrokenAttachment(Window window, Attachment state, Exception error)
    {
        // Never return a false "ordinary window" result while a native child may remain.
        // Destroy only our own component HWND as a last resort; the App can recreate it while
        // the independent main window and collector remain alive. Avoid recursive Closed cleanup.
        _desktopAttachmentDisabled = true; // A replacement widget must fall back instead of retrying this host in a loop.
        state.Recovering = true;
        bool destroyed = !IsWindow(state.Handle) || DestroyWindow(state.Handle);
        if (destroyed || !IsWindow(state.Handle)) Attachments.Remove(window);
        else state.Recovering = false; // Retain recovery state when destruction itself failed.
        throw new InvalidOperationException(destroyed
            ? "桌面连接未能复原，组件窗口已安全关闭，请重新打开组件。"
            : "桌面连接未能复原，组件窗口仍需恢复，请退出后重新打开程序。", error);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref NativePoint points, uint count);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", EntryPoint = "SetLastError")] private static extern void NativeSetLastError(uint error);
}
