using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScreenTime;

/// <summary>Optional XInput only: 5s disconnected discovery, 250ms connected input samples.</summary>
internal sealed class GamepadMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _input;
    private bool _enabled;
    public int ConnectedCount { get; private set; }
    public string Status { get; private set; } = "off";
    public GamepadMonitor(Action input)
    {
        _input = input;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
    }
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _timer.Stop(); ConnectedCount = 0;
        Status = enabled ? "disconnected" : "off";
        if (enabled) { Poll(); if (_enabled) _timer.Start(); }
    }
    private void Poll()
    {
        if (!_enabled) return;
        try
        {
            int connected = 0; bool input = false;
            for (uint index = 0; index < 4; index++)
                if (XInputGetState(index, out var state) == 0)
                {
                    connected++;
                    var g = state.Gamepad;
                    input |= ActivityEvidence.MeaningfulGamepad(g.Buttons, g.LeftTrigger, g.RightTrigger,
                        g.LeftX, g.LeftY, g.RightX, g.RightY);
                }
            ConnectedCount = connected;
            Status = connected > 0 ? "connected:" + connected : "disconnected";
            _timer.Interval = connected > 0 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5);
            if (input) _input();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { Status = "unavailable"; _timer.Stop(); _enabled = false; }
    }
    public void Dispose() { _timer.Stop(); _enabled = false; }
    [StructLayout(LayoutKind.Sequential)] private struct Pad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Pad Gamepad; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
}
