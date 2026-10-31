using System;
using System.IO;
using System.Security;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScreenTime;

/// <summary>One hidden HWND on the existing UI dispatcher. No polling, extra thread or WinRT projection.</summary>
internal sealed class SystemThemeMonitor : IDisposable
{
    private readonly HwndSource _source;
    private readonly Func<bool>? _reader;
    private bool _queued, _disposed;
    public bool IsDark { get; private set; }
    public event Action? Changed;
    internal IntPtr Handle => _source.Handle;
    public SystemThemeMonitor(Func<bool>? reader = null)
    {
        _reader = reader;
        IsDark = Read();
        _source = new HwndSource(new HwndSourceParameters("ScreenTime.Appearance")
        { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ParentWindow = IntPtr.Zero });
        _source.AddHook(WindowMessage);
    }
    private bool Read()
    {
        if (_reader != null) return _reader();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { return IsDark; }
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && (message is 0x001A or 0x031A || (message == 0x0218 && wParam.ToInt64() == 0x12)))
        {
            if (!_queued)
            {
                _queued = true;
                _source.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _queued = false;
                    if (_disposed) return;
                    bool dark = Read();
                    if (dark == IsDark) return;
                    IsDark = dark;
                    Changed?.Invoke();
                }), DispatcherPriority.Background);
            }
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WindowMessage);
        _source.Dispose();
        Changed = null;
    }
}
