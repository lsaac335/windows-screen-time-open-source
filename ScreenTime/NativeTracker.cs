using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Interop;

namespace ScreenTime;

/// <summary>
/// Collects metadata only, on its owning dispatcher thread. No titles, input hooks,
/// screenshots or network traffic are used. The owner calls Tick once a second.
/// </summary>
public sealed class NativeTracker : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly int _sessionId;
    private readonly NativeMethods.WinEventDelegate _foregroundCallback;
    private readonly Dictionary<string, AppIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _applicationPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _idleOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ForegroundEventClock _eventClock = new(Stopwatch.Frequency);
    private readonly Queue<(IntPtr Window, uint Time)> _foregroundEvents = new();
    private bool _handlingForeground;
    private IMediaSessionMonitor? _media;
    private readonly GamepadMonitor _gamepad;
    private HashSet<string> _playingSources = new(StringComparer.OrdinalIgnoreCase);
    private string? _foregroundPath, _foregroundAumid;
    private bool _mediaMatched;
    private long? _gamepadInputStamp;
    private IntPtr _powerNotification;
    private IntPtr _foregroundHook;
    private bool _sessionNotifications;
    private bool _started;
    private bool _disposed;
    private bool _suspended;
    private bool _sessionKnown;
    private bool _lockKnown;
    private bool _locked;
    private bool _localActive;
    private bool _remote;
    private bool _disconnected;
    private string? _callbackError;
    private int? _displayState;
    private int _powerRegistrationError;
    private long _stamp;
    private DateTimeOffset _wall;
    private Snapshot _snapshot = new("unknown", "", "");
    private bool _idleFilterEnabled;
    private int _idleTimeoutMinutes;
    private int _lateEventCount;
    private string? _timingIssue;
    private long _timingIssueStamp;
    public int IdleTimeoutMinutes => _idleTimeoutMinutes;
    public int EffectiveIdleTimeoutMinutes => _idleOverrides.TryGetValue(_snapshot.Id, out int minutes) ? minutes : _idleTimeoutMinutes;
    public double? LastForegroundEventDelayMilliseconds { get; private set; }
    public bool TimingDegraded => _timingIssue != null;
    public string TimingDiagnostic => _timingIssue ?? "event-time";
    private bool FilterCurrentApp => _idleFilterEnabled && EffectiveIdleTimeoutMinutes > 0;
    public double? InputIdleSeconds { get; private set; }
    private bool GamepadRecent => _gamepadInputStamp.HasValue &&
        (Stopwatch.GetTimestamp() - _gamepadInputStamp.Value) / (double)Stopwatch.Frequency < EffectiveIdleTimeoutMinutes * 60d;
    public bool IsIdle => FilterCurrentApp && InputIdleSeconds >= EffectiveIdleTimeoutMinutes * 60d &&
        _snapshot.Kind == "app" && !_mediaMatched && !GamepadRecent;

    public event Action<UsageSegment>? SegmentReady;
    public event Action? StateChanged;
    public event Action<Exception>? Faulted;

    public bool IsPaused { get; private set; }
    public bool DisplayKnown => _displayState.HasValue;
    public bool PowerNotificationsRegistered => _powerNotification != IntPtr.Zero;
    public bool SessionNotificationsRegistered => _sessionNotifications;
    public string CurrentAppName => IsIdle ? "空闲已排除" : _snapshot.Kind == UsageRules.ScreenOnlyKind ? "本软件已排除" : _snapshot.Kind == "app" ? _snapshot.Name : "—";
    public string DisplayStateText => _displayState switch
    {
        0 => "已关闭",
        1 => "已点亮",
        2 => "已变暗",
        _ => "等待系统状态"
    };

    public string StatusText
    {
        get
        {
            if (_disposed) return "已停止";
            if (!_started) return "尚未开始";
            if (IsPaused) return "已暂停";
            if (_callbackError != null) return "采集异常 · " + _callbackError;
            if (_suspended) return "电脑休眠中";
            if (_remote) return "远程会话暂不支持";
            if (!_sessionKnown || !_lockKnown) return "等待会话状态";
            if (!_localActive) return "当前本地会话未活动";
            if (!_displayState.HasValue)
                return _powerNotification == IntPtr.Zero ? "显示状态监听异常" : "等待显示状态";
            if (_displayState == 0) return "屏幕已关闭";
            if (_locked) return "记录中 · 已锁屏";
            if (FilterCurrentApp && !InputIdleSeconds.HasValue) return "空闲检测异常 · 按前台计时";
            if (_foregroundHook == IntPtr.Zero) return "切换监听异常 · 定时校验中";
            if (TimingDegraded) return "计时校正中 · 部分区间未记录";
            if (IsIdle) return "空闲中 · 活跃计时已暂停";
            if (_snapshot.Kind == "app" && FilterCurrentApp && InputIdleSeconds >= EffectiveIdleTimeoutMinutes * 60d)
            {
                if (_mediaMatched) return "记录中 · 媒体推测";
                if (GamepadRecent) return "记录中 · 手柄输入";
            }
            return "正在记录";
        }
    }

    public string DiagnosticText =>
        $"display={(_displayState.HasValue ? _displayState.Value.ToString() : "unknown")}; " +
        $"powerNotify={PowerNotificationsRegistered}; powerError={_powerRegistrationError}; " +
        $"sessionNotify={_sessionNotifications}; sessionKnown={_sessionKnown}; " +
        $"lockKnown={_lockKnown}; locked={_locked}; localActive={_localActive}; " +
        $"remote={_remote}; foregroundHook={(_foregroundHook != IntPtr.Zero)}; callbackFault={(_callbackError != null)}; " +
        $"idleFilter={_idleFilterEnabled}; idleTimeoutMinutes={EffectiveIdleTimeoutMinutes}; " +
        $"inputIdleSeconds={InputIdleSeconds?.ToString("F1") ?? "unknown"}; " +
        $"foregroundDelayMs={LastForegroundEventDelayMilliseconds?.ToString("F1") ?? "unknown"}; " +
        $"lateEvents={_lateEventCount}; timing={TimingDiagnostic}; media={_media?.Status ?? "off"}; mediaMatched={_mediaMatched}; gamepad={_gamepad.Status}";

    /// <summary>For local icon extraction only. Paths are never added to usage segments or files.</summary>
    public string? GetApplicationPath(string appId)
    {
        if (appId == "system-desktop")
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return _applicationPaths.TryGetValue(appId, out string? path) ? path : null;
    }

    public NativeTracker(IntPtr hwnd, bool idleFilterEnabled = true, int idleTimeoutMinutes = 5,
        IReadOnlyDictionary<string, int>? idleOverrides = null)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("需要有效窗口句柄。", nameof(hwnd));
        _hwnd = hwnd;
        _idleFilterEnabled = idleFilterEnabled;
        ValidateTimeout(idleTimeoutMinutes, false);
        _idleTimeoutMinutes = idleTimeoutMinutes;
        if (idleOverrides != null)
            foreach (var rule in idleOverrides)
            {
                if (string.IsNullOrWhiteSpace(rule.Key)) throw new ArgumentException("应用标识不能为空。", nameof(idleOverrides));
                ValidateTimeout(rule.Value, true);
                _idleOverrides[rule.Key] = rule.Value;
            }
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("无法连接窗口消息。 ");
        _sessionId = Process.GetCurrentProcess().SessionId;
        _foregroundCallback = ForegroundChanged;
        _gamepad = new GamepadMonitor(() =>
        {
            if (_disposed || !_started) return;
            Commit();
            // A controller has no HWND ownership. Accept its input only for the app currently
            // holding foreground focus, never as evidence across a subsequent app switch.
            if (_snapshot.Kind == "app") _gamepadInputStamp = Stopwatch.GetTimestamp();
            StateChanged?.Invoke();
        });
        _source.AddHook(WindowMessage);
    }

    public void SetMediaDetection(bool enabled)
    {
        if (enabled)
        {
            _media ??= MediaSessionMonitor.Create(MediaChanged);
            _media.SetEnabled(true);
        }
        else if (_media != null)
        {
            _media.SetEnabled(false);
            _media.Dispose(); _media = null;
        }
    }
    private void MediaChanged(HashSet<string> sources)
    {
        if (_disposed) return;
        if (_started) Commit();
        _playingSources = sources;
        MatchMedia();
        StateChanged?.Invoke();
    }
    public void SetGamepadDetection(bool enabled)
    {
        if (_started) Commit();
        _gamepadInputStamp = null;
        _gamepad.SetEnabled(enabled);
        StateChanged?.Invoke();
    }
    private void MatchMedia() => _mediaMatched = _snapshot.Kind == "app" &&
        _playingSources.Any(source => ActivityEvidence.SourceMatches(source, _foregroundPath, _foregroundAumid));

    public void Start(bool paused)
    {
        ThrowIfDisposed();
        if (_started) return;
        IsPaused = paused;
        _started = true;
        InputIdleSeconds = ReadInputIdleSeconds();
        _wall = DateTimeOffset.Now;
        _stamp = Stopwatch.GetTimestamp();
        _eventClock.Reset(Environment.TickCount64, _stamp);
        RefreshSession();
        _sessionNotifications = NativeMethods.WTSRegisterSessionNotification(_hwnd, 0);
        RegisterDisplayNotification();
        _foregroundHook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero,
            _foregroundCallback, 0, 0, 0);
        Capture();
        StateChanged?.Invoke();
    }

    /// <summary>Must be called on the same sampling thread as the constructor.</summary>
    public void Tick()
    {
        if (!_started || _disposed) return;
        Commit();
        RefreshSession();
        Capture();
        _callbackError = null;
        if (_timingIssue != null && (Stopwatch.GetTimestamp() - _timingIssueStamp) / (double)Stopwatch.Frequency >= 5)
            _timingIssue = null;
    }

    public void SetPaused(bool paused)
    {
        ThrowIfDisposed();
        if (!_started || paused == IsPaused) return;
        Commit();
        IsPaused = paused;
        RefreshSession();
        Capture();
        StateChanged?.Invoke();
    }

    public void SetIdleFilter(bool enabled)
    {
        ThrowIfDisposed();
        if (_idleFilterEnabled == enabled) return;
        if (_started) Commit();
        _idleFilterEnabled = enabled;
        StateChanged?.Invoke();
    }

    public void SetIdleTimeoutMinutes(int minutes)
    {
        ThrowIfDisposed();
        ValidateTimeout(minutes, false);
        if (_idleTimeoutMinutes == minutes) return;
        if (_started) Commit();
        _idleTimeoutMinutes = minutes;
        StateChanged?.Invoke();
    }

    public void SetAppIdleTimeoutMinutes(string appId, int? minutes)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("应用标识不能为空。", nameof(appId));
        if (minutes.HasValue) ValidateTimeout(minutes.Value, true);
        if (_started) Commit();
        if (minutes.HasValue) _idleOverrides[appId] = minutes.Value;
        else _idleOverrides.Remove(appId);
        StateChanged?.Invoke();
    }

    private static void ValidateTimeout(int minutes, bool allowNever)
    {
        if (minutes is not (1 or 3 or 5 or 10 or 15 or 30) && !(allowNever && minutes == 0))
            throw new ArgumentOutOfRangeException(nameof(minutes), "空闲阈值必须为 1、3、5、10、15 或 30 分钟。");
    }

    private static double? ReadInputIdleSeconds()
    {
        var input = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref input)) return null;
        // LASTINPUTINFO uses a 32-bit tick count; unsigned subtraction handles wrap.
        uint milliseconds = unchecked((uint)Environment.TickCount64 - input.Time);
        // An input timestamp in the future is an invalid sample, not 49 days idle.
        return milliseconds > int.MaxValue ? null : milliseconds / 1000d;
    }

    /// <summary>Ends the current interval once; a fresh baseline prevents duplicate records.</summary>
    public void Flush()
    {
        if (!_started || _disposed) return;
        Commit();
        RefreshSession();
        Capture();
    }

    private void RegisterDisplayNotification()
    {
        if (_powerNotification != IntPtr.Zero)
            NativeMethods.UnregisterPowerSettingNotification(_powerNotification);
        _displayState = null;
        Guid guid = NativeMethods.SessionDisplayStatus;
        _powerNotification = NativeMethods.RegisterPowerSettingNotification(_hwnd, ref guid, 0);
        _powerRegistrationError = _powerNotification == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
    }

    private void RefreshSession()
    {
        bool connectKnown = TryQueryInt(8, out int connectState);
        bool protocolKnown = TryQueryProtocol(out ushort protocol);
        _remote = protocolKnown ? protocol != 0 : NativeMethods.GetSystemMetrics(0x1000) != 0;
        uint consoleSession = NativeMethods.WTSGetActiveConsoleSessionId();
        _localActive = consoleSession == (uint)_sessionId && connectKnown && connectState == 0 &&
            !_remote && !_disconnected;
        _sessionKnown = connectKnown && protocolKnown && consoleSession != uint.MaxValue;

        IntPtr data = IntPtr.Zero;
        try
        {
            // Native WTSINFOEXW contains DWORD Level followed by an 8-byte aligned
            // union. The first three fields in its level-1 payload are DWORDs.
            if (NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, _sessionId, 25,
                    out data, out int bytes) && bytes >= 20 && Marshal.ReadInt32(data) == 1)
            {
                int flags = Marshal.ReadInt32(data, 16);
                _lockKnown = flags == 0 || flags == 1;
                if (_lockKnown) _locked = flags == 0;
            }
            else
            {
                _lockKnown = false;
            }
        }
        finally
        {
            if (data != IntPtr.Zero) NativeMethods.WTSFreeMemory(data);
        }
    }

    private bool TryQueryInt(int infoClass, out int value)
    {
        value = 0;
        IntPtr data = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, _sessionId, infoClass,
                    out data, out int bytes) || bytes < 4) return false;
            value = Marshal.ReadInt32(data);
            return true;
        }
        finally
        {
            if (data != IntPtr.Zero) NativeMethods.WTSFreeMemory(data);
        }
    }

    private bool TryQueryProtocol(out ushort value)
    {
        value = 0;
        IntPtr data = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, _sessionId, 16,
                    out data, out int bytes) || bytes < 2) return false;
            value = unchecked((ushort)Marshal.ReadInt16(data));
            return true;
        }
        finally
        {
            if (data != IntPtr.Zero) NativeMethods.WTSFreeMemory(data);
        }
    }

    private void Capture(IntPtr? eventWindow = null)
    {
        Snapshot previous = _snapshot;
        if (IsPaused) _snapshot = new("paused", "", "");
        else if (_suspended) _snapshot = new("screenoff", "", "");
        else if (!_sessionKnown || !_lockKnown || !_localActive || !_displayState.HasValue)
            _snapshot = new("unknown", "", "");
        else if (_displayState == 0) _snapshot = new("screenoff", "", "");
        else if (_locked) _snapshot = new("locked", "", "");
        else
        {
            AppIdentity app = ResolveForeground(eventWindow ?? NativeMethods.GetForegroundWindow());
            // Viewing this dashboard is still observed screen-on time, without app attribution.
            _snapshot = app.Id == UsageRules.SelfAppId
                ? new(UsageRules.ScreenOnlyKind, "", "")
                : new("app", app.Id, app.Name);
        }
        if (previous != _snapshot) _gamepadInputStamp = null;
        MatchMedia();
        if (previous != _snapshot) StateChanged?.Invoke();
    }

    private void Commit(bool forceGap = false)
    {
        long nowStamp = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.Now;
        CommitAt(nowStamp, now, ReadInputIdleSeconds(), forceGap);
    }

    private void CommitAt(long endStamp, DateTimeOffset observedEnd, double? endIdleSeconds, bool forceGap = false)
    {
        // All producers share this cursor. A stale notification can never revise a stored interval.
        if (endStamp < _stamp) return;
        bool wasIdle = IsIdle;
        double? previousIdleSeconds = InputIdleSeconds;
        InputIdleSeconds = endIdleSeconds;
        double elapsed = (endStamp - _stamp) / (double)Stopwatch.Frequency;
        DateTimeOffset start = _wall;
        DateTimeOffset end = start.AddTicks((long)Math.Round(elapsed * TimeSpan.TicksPerSecond));
        bool wallClockChanged = Math.Abs((observedEnd - end).TotalSeconds) > 2;
        _stamp = endStamp;
        // Keep exact contiguous boundaries under normal operation so thousands
        // of one-second ticks compact into one interval. Only re-anchor after
        // a real wall-clock jump; elapsed time itself always comes from QPC.
        _wall = wallClockChanged ? observedEnd : end;
        if (wasIdle != IsIdle) StateChanged?.Invoke();
        if (elapsed <= 0.0001) return;

        // Dispatcher stalls / sleep / debugging are not evidence that the last
        // foreground application stayed in front. Never backfill those gaps.
        bool gap = forceGap || elapsed > 3.5 || wallClockChanged;
        var segment = new UsageSegment
        {
            Start = start,
            End = end,
            Seconds = elapsed,
            Kind = gap ? "gap" : _snapshot.Kind,
            AppId = gap ? "" : _snapshot.Id,
            AppName = gap ? "" : _snapshot.Name,
            ActivityTracked = true,
            ActivityBasis = !FilterCurrentApp ? "foreground" : "input"
        };
        foreach (var part in IdleTimeFilter.Split(segment, FilterCurrentApp, previousIdleSeconds,
                     InputIdleSeconds, Math.Max(1, EffectiveIdleTimeoutMinutes) * 60d))
        {
            double? padEnd = _gamepadInputStamp.HasValue ?
                Math.Max(0, (endStamp - _gamepadInputStamp.Value) / (double)Stopwatch.Frequency - (end - part.End).TotalSeconds) : null;
            double? padStart = padEnd.HasValue ? Math.Max(0, padEnd.Value - (part.End - part.Start).TotalSeconds) : null;
            foreach (var supplemented in ActivityEvidence.Supplement(part, _mediaMatched, padStart, padEnd,
                         Math.Max(1, EffectiveIdleTimeoutMinutes) * 60d))
                SegmentReady?.Invoke(supplemented);
        }
        if (gap)
        {
            SetTimingIssue(wallClockChanged ? "wall-clock-adjusted" : "unobserved-interval");
            // Sleep and debugger stops can change the relation between the two clocks.
            long currentStamp = Stopwatch.GetTimestamp();
            _eventClock.Reset(Environment.TickCount64, currentStamp);
        }
    }

    private void ForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (!_started || _disposed || IsPaused || _suspended) return;
        // OUTOFCONTEXT callbacks are delivered on the installing sampling thread.
        // APIs called while resolving an identity may reenter this hook; preserve delivery order.
        _foregroundEvents.Enqueue((hwnd, eventTime));
        if (_handlingForeground) return;
        _handlingForeground = true;
        try
        {
            while (_foregroundEvents.Count > 0)
            {
                var item = _foregroundEvents.Dequeue();
                HandleForegroundEvent(item.Window, item.Time);
            }
        }
        catch (Exception ex) { _foregroundEvents.Clear(); ReportNativeFault(ex); }
        finally { _handlingForeground = false; }
    }

    private void HandleForegroundEvent(IntPtr window, uint eventTime)
    {
        long nowStamp = Stopwatch.GetTimestamp();
        long nowTick = Environment.TickCount64;
        DateTimeOffset nowWall = DateTimeOffset.Now;
        double? observedIdle = ReadInputIdleSeconds();
        bool mapped = _eventClock.TryMap(eventTime, nowTick, nowStamp, _stamp,
            out long boundary, out double delay, out string reason);
        LastForegroundEventDelayMilliseconds = delay;
        if (!mapped || window == IntPtr.Zero)
        {
            _lateEventCount++;
            // The missing transition cannot be reassigned after persistence. Mark only the
            // uncommitted suffix unknown, then resume from the presently observable window.
            CommitAt(nowStamp, nowWall, observedIdle, forceGap: true);
            Capture();
            SetTimingIssue(mapped ? "missing-event-window" : reason);
            return;
        }

        double prefixSeconds = (boundary - _stamp) / (double)Stopwatch.Frequency;
        double delaySeconds = (nowStamp - boundary) / (double)Stopwatch.Frequency;
        bool idleKnown = ForegroundEventClock.TryProjectIdle(InputIdleSeconds, observedIdle,
            prefixSeconds, delaySeconds, FilterCurrentApp ? EffectiveIdleTimeoutMinutes * 60d : 0,
            out double? boundaryIdle);
        bool uncertainActivity = FilterCurrentApp && _snapshot.Kind == "app" && !idleKnown;
        CommitAt(boundary, nowWall.AddSeconds(-delaySeconds), boundaryIdle, uncertainActivity);
        // Use the HWND supplied with this event. Reading GetForegroundWindow here would
        // collapse queued A -> B -> C transitions into C and lose the time spent in B.
        Capture(window);
        if (uncertainActivity) SetTimingIssue("delayed-input-boundary");
    }

    private void SetTimingIssue(string reason)
    {
        _timingIssue = reason;
        _timingIssueStamp = Stopwatch.GetTimestamp();
        StateChanged?.Invoke();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (!_started || _disposed) return IntPtr.Zero;
        try
        {
          if (message == 0x0218) // WM_POWERBROADCAST
          {
            int powerEvent = unchecked((int)wParam.ToInt64());
            if (powerEvent == 0x8013 && lParam != IntPtr.Zero) // PBT_POWERSETTINGCHANGE
            {
                Guid setting = Marshal.PtrToStructure<Guid>(lParam);
                int length = Marshal.ReadInt32(lParam, 16);
                if (setting == NativeMethods.SessionDisplayStatus && length >= 4)
                {
                    Commit();
                    int state = Marshal.ReadInt32(lParam, 20);
                    _displayState = state >= 0 && state <= 2 ? state : null;
                    Capture();
                    StateChanged?.Invoke();
                }
            }
            else if (powerEvent == 4) // PBT_APMSUSPEND
            {
                Commit();
                _suspended = true;
                Capture();
                StateChanged?.Invoke();
            }
            else if (powerEvent == 6 || powerEvent == 7 || powerEvent == 18)
            {
                Commit(forceGap: true);
                _suspended = false;
                RefreshSession();
                RegisterDisplayNotification();
                Capture();
                StateChanged?.Invoke();
            }
          }
          else if (message == 0x02B1 && lParam.ToInt64() == _sessionId) // WM_WTSSESSION_CHANGE
          {
            Commit();
            int change = unchecked((int)wParam.ToInt64());
            if (change == 2 || change == 3 || change == 6) _disconnected = true;
            if (change == 1) _disconnected = false;
            RefreshSession();
            if (change == 7) { _locked = true; _lockKnown = true; }
            if (change == 8) { _locked = false; _lockKnown = true; }
            if (change == 1) RegisterDisplayNotification();
            Capture();
            StateChanged?.Invoke();
          }
        }
        catch (Exception ex) { ReportNativeFault(ex); }
        return IntPtr.Zero;
    }

    private void ReportNativeFault(Exception error)
    {
        // No managed exception may escape a WinEvent callback / WndProc hook.
        _callbackError = error.Message;
        _snapshot = new("unknown", "", "");
        try { Faulted?.Invoke(error); } catch { }
        try { StateChanged?.Invoke(); } catch { }
    }

    private AppIdentity ResolveForeground(IntPtr foreground)
    {
        _foregroundPath = null; _foregroundAumid = null;
        if (foreground == IntPtr.Zero) return UnknownApp;
        NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0) return UnknownApp;
        // Check ownership before resolving paths/names. Other software with the same filename
        // is not this process and must remain eligible for usage tracking.
        if (processId == Environment.ProcessId) return SelfApp;
        StringBuilder className = new(256);
        NativeMethods.GetClassName(foreground, className, className.Capacity);
        string windowClass = className.ToString();

        if (windowClass == "ApplicationFrameWindow")
        {
            uint frameProcessId = processId;
            NativeMethods.EnumChildWindows(foreground, (child, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(child, out uint childPid);
                StringBuilder childClass = new(128);
                NativeMethods.GetClassName(child, childClass, childClass.Capacity);
                if (childPid != 0 && childPid != frameProcessId &&
                    childClass.ToString() == "Windows.UI.Core.CoreWindow")
                {
                    processId = childPid;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        string? path = TryProcessPath(processId);
        _foregroundPath = path;
        _foregroundAumid = TryProcessAumid(processId);
        string processName;
        try
        {
            if (path == null)
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            else processName = Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
        {
            return UnknownApp;
        }

        if ((processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
             windowClass != "CabinetWClass" && windowClass != "ExploreWClass") ||
            processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
            return new("system-desktop", "系统 / 桌面");

        string cacheKey = path ?? "process:" + processName;
        if (_identities.TryGetValue(cacheKey, out AppIdentity? cached))
        {
            if (path != null) _applicationPaths[cached.Id] = path;
            return cached;
        }
        string name = FriendlyName(processName);
        if (path != null && name == processName)
        {
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                string? candidate = !string.IsNullOrWhiteSpace(version.ProductName) ?
                    version.ProductName : version.FileDescription;
                if (!string.IsNullOrWhiteSpace(candidate)) name = candidate.Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception) { }
        }
        if (name.Length > 120) name = name.Substring(0, 120);
        string identityPath = cacheKey.ToUpperInvariant();
        // Remove a WindowsApps package's version (retain package name, arch and
        // publisher) so a packaged app update does not create a fresh app entry.
        identityPath = Regex.Replace(identityPath, @"(\\WINDOWSAPPS\\[^\\_]+)_\d+\.\d+\.\d+\.\d+_", "$1_", RegexOptions.CultureInvariant);
        string id = "exe:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath))).Substring(0, 24);
        AppIdentity resolved = new(id, name);
        if (_identities.Count > 256) _identities.Clear();
        _identities[cacheKey] = resolved;
        if (_applicationPaths.Count > 512) _applicationPaths.Clear();
        if (path != null) _applicationPaths[id] = path;
        return resolved;
    }

    private static readonly AppIdentity UnknownApp = new("unknown-app", "未识别应用");
    private static readonly AppIdentity SelfApp = new(UsageRules.SelfAppId, "屏幕时间");

    private static string FriendlyName(string processName) => processName.ToLowerInvariant() switch
    {
        "msedge" => "Microsoft Edge",
        "chrome" => "Google Chrome",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "wechat" or "weixin" => "微信",
        "qq" => "QQ",
        "code" => "Visual Studio Code",
        "notepad" => "记事本",
        "windowsterminal" => "Windows 终端",
        "powershell" or "pwsh" => "PowerShell",
        "cmd" => "命令提示符",
        "explorer" => "文件资源管理器",
        "taskmgr" => "任务管理器",
        "codex" => "Codex",
        "systemsettings" => "Windows 设置",
        _ => processName
    };

    private static string? TryProcessPath(uint processId)
    {
        IntPtr process = NativeMethods.OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            int capacity = 32768;
            StringBuilder path = new(capacity);
            return NativeMethods.QueryFullProcessImageName(process, 0, path, ref capacity) ? path.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private static string? TryProcessAumid(uint processId)
    {
        IntPtr process = NativeMethods.OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            uint count = 0;
            if (NativeMethods.GetApplicationUserModelId(process, ref count, null) != 122 || count is 0 or > 1024) return null;
            var value = new StringBuilder((int)count);
            return NativeMethods.GetApplicationUserModelId(process, ref count, value) == 0 ? value.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeTracker));
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_started) Commit();
        }
        finally
        {
            _started = false;
            _disposed = true;
            _media?.Dispose();
            _gamepad.Dispose();
            if (_powerNotification != IntPtr.Zero)
            {
                NativeMethods.UnregisterPowerSettingNotification(_powerNotification);
                _powerNotification = IntPtr.Zero;
            }
            if (_sessionNotifications) NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
            if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
            if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
            _identities.Clear();
            _applicationPaths.Clear();
        }
    }

    private sealed record AppIdentity(string Id, string Name);
    private sealed record Snapshot(string Kind, string Id, string Name);
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(IntPtr process, ref uint count, StringBuilder? id);
    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo { internal uint Size; internal uint Time; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInputInfo info);
    internal static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);
    internal delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder className, int count);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsDelegate callback, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId,
        int infoClass, out IntPtr data, out int bytes);
    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr data);
    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);
}
