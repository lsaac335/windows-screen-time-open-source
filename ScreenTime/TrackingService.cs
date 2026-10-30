using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenTime;

/// <summary>Owns native hooks, their message window and the sampling clock on one STA thread.</summary>
public sealed class TrackingService : IDisposable
{
    private readonly ConcurrentQueue<UsageSegment> _segments = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher? _dispatcher;
    private NativeTracker? _tracker;
    private HwndSource? _source;
    private DispatcherTimer? _timer;
    private Exception? _startupError;
    private volatile TrackingSnapshot _snapshot = new("正在初始化", "等待开始记录", "尚未采集", "", null, false, 0, 0);
    private string? _samplingError;
    private int _ticks;
    private bool _disposed;
    private volatile bool _stopped;

    public string StatusText => _snapshot.Status;
    public string CurrentAppName => _snapshot.App;
    public string DisplayStateText => _snapshot.Display;
    public string DiagnosticText => _snapshot.Diagnostic;
    public double? InputIdleSeconds => _snapshot.IdleSeconds;
    public bool IsIdle => _snapshot.IsIdle;
    public int SamplerThreadId => _snapshot.ThreadId;
    public int SampleCount => _snapshot.Ticks;
    public int PendingSegments => _segments.Count;

    public TrackingService(bool idleFilter, int timeoutMinutes, IReadOnlyDictionary<string, int> overrides, bool paused)
    {
        var copiedRules = new Dictionary<string, int>(overrides, StringComparer.OrdinalIgnoreCase);
        _thread = new Thread(() => Run(idleFilter, timeoutMinutes, copiedRules, paused))
        { IsBackground = true, Name = "ScreenTime.NativeSampler" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError != null)
        {
            _thread.Join();
            _ready.Dispose();
            throw new InvalidOperationException("独立采集线程未能启动。", _startupError);
        }
    }

    private void Run(bool filter, int timeoutMinutes, IReadOnlyDictionary<string, int> overrides, bool paused)
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            // Hidden top-level HWND receives WTS / display / power broadcasts independently of the UI.
            _source = new HwndSource(new HwndSourceParameters("ScreenTime.NativeSampler")
            { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ParentWindow = IntPtr.Zero });
            _tracker = new NativeTracker(_source.Handle, filter, timeoutMinutes, overrides);
            _tracker.SegmentReady += segment => _segments.Enqueue(segment);
            _tracker.Faulted += ex => { _samplingError = ex.Message; Publish(); };
            _tracker.StateChanged += Publish;
            _tracker.Start(paused);
            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                try { _tracker.Tick(); _ticks++; _samplingError = null; }
                catch (Exception ex) { _samplingError = ex.Message; }
                Publish();
            };
            _timer.Start();
            Publish();
            _ready.Set();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _samplingError = ex.Message;
            Publish();
            _ready.Set();
        }
        finally
        {
            _timer?.Stop();
            try { _tracker?.Dispose(); }
            catch (Exception ex) { _samplingError = ex.Message; Publish(); }
            finally
            {
                try { _source?.Dispose(); }
                finally { _stopped = true; }
            }
        }
    }

    private void Publish()
    {
        if (_tracker == null) return;
        _snapshot = new TrackingSnapshot(_samplingError == null ? _tracker.StatusText : "采集异常 · " + _samplingError,
            _tracker.CurrentAppName, _tracker.DisplayStateText,
            "samplerThread=" + Environment.CurrentManagedThreadId + "; ticks=" + _ticks + "; " + _tracker.DiagnosticText,
            _tracker.InputIdleSeconds, _tracker.IsIdle, Environment.CurrentManagedThreadId, _ticks);
    }

    private void Invoke(Action<NativeTracker> action)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TrackingService));
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("采集尚未启动。");
        if (_stopped || !_thread.IsAlive || dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("采集线程已停止，请重启程序。");
        dispatcher.Invoke(() => { action(_tracker!); Publish(); }, DispatcherPriority.Normal,
            CancellationToken.None, TimeSpan.FromSeconds(5));
    }

    public void SetPaused(bool paused) => Invoke(t => t.SetPaused(paused));
    public void SetIdleFilter(bool enabled) => Invoke(t => t.SetIdleFilter(enabled));
    public void SetIdleTimeoutMinutes(int minutes) => Invoke(t => t.SetIdleTimeoutMinutes(minutes));
    public void SetAppIdleTimeoutMinutes(string appId, int? minutes) => Invoke(t => t.SetAppIdleTimeoutMinutes(appId, minutes));
    public void Flush() => Invoke(t => t.Flush());
    public string? GetApplicationPath(string appId)
    {
        string? path = null;
        Invoke(t => path = t.GetApplicationPath(appId));
        return path;
    }

    // Only the UI thread consumes this queue. Persistence failures leave the head available for retry.
    public void DrainSegments(Action<UsageSegment> consume)
    {
        while (_segments.TryPeek(out var segment))
        {
            consume(segment);
            _segments.TryDequeue(out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_dispatcher != null && !_stopped && _thread.IsAlive && !_dispatcher.HasShutdownStarted)
                _dispatcher.Invoke(() =>
                {
                    _timer?.Stop();
                    try { _tracker?.Dispose(); }
                    finally { _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                });
        }
        finally
        {
            _disposed = true;
            _thread.Join();
            _ready.Dispose();
        }
    }

    private sealed record TrackingSnapshot(string Status, string App, string Display, string Diagnostic,
        double? IdleSeconds, bool IsIdle, int ThreadId, int Ticks);
}
