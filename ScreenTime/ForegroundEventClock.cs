using System;

namespace ScreenTime;

/// <summary>Maps WinEvent's wrapping millisecond clock to one ordered QPC timeline.</summary>
internal sealed class ForegroundEventClock
{
    internal const int MaximumDelayMilliseconds = 3500;
    // WinEvent/GetTickCount resolution can be coarser than QPC. This is a clamp,
    // never permission to move an already committed boundary backwards.
    internal const int QuantizationToleranceMilliseconds = 20;
    private readonly long _frequency;
    private long _anchorTick, _anchorStamp;
    private long? _lastEventTick;

    internal ForegroundEventClock(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency;
    }

    internal void Reset(long tickMilliseconds, long stamp)
    {
        _anchorTick = tickMilliseconds;
        _anchorStamp = stamp;
        // After a gap we recovered the currently visible window; older queued
        // notifications must not replace that recovery snapshot with stale HWNDs.
        _lastEventTick = tickMilliseconds;
    }

    internal bool TryMap(uint eventTime, long nowTick, long nowStamp, long committedStamp,
        out long stamp, out double delayMilliseconds, out string reason)
    {
        stamp = committedStamp;
        long age = unchecked((int)(unchecked((uint)nowTick) - eventTime));
        delayMilliseconds = age;
        reason = "";
        if (age < 0) { reason = "future-event"; return false; }
        if (age > MaximumDelayMilliseconds) { reason = "stale-event"; return false; }
        long tick = nowTick - age;
        if (_lastEventTick.HasValue && tick < _lastEventTick.Value)
        { reason = "out-of-order-event"; return false; }
        long mapped = _anchorStamp + (long)Math.Round((tick - _anchorTick) * (_frequency / 1000d));
        long tolerance = (long)Math.Ceiling(QuantizationToleranceMilliseconds * (_frequency / 1000d));
        if (mapped > nowStamp + tolerance) { reason = "event-clock-drift"; return false; }
        if (mapped < committedStamp - tolerance) { reason = "event-before-commit"; return false; }
        stamp = Math.Clamp(mapped, committedStamp, Math.Max(committedStamp, nowStamp));
        _lastEventTick = tick;
        return true;
    }

    /// <summary>
    /// The latest input may be newer than a delayed foreground event. Only project
    /// it backwards when it was already observable at that boundary. Otherwise an
    /// older sample is enough only when even no input would leave the prefix active.
    /// </summary>
    internal static bool TryProjectIdle(double? previousIdleSeconds, double? observedIdleSeconds,
        double prefixSeconds, double delaySeconds, double timeoutSeconds, out double? boundaryIdleSeconds)
    {
        boundaryIdleSeconds = null;
        if (!Valid(observedIdleSeconds)) return false;
        double historicalAge = observedIdleSeconds!.Value - Math.Max(0, delaySeconds);
        if (historicalAge >= 0)
        {
            boundaryIdleSeconds = historicalAge;
            return true;
        }
        if (!Valid(previousIdleSeconds)) return false;
        double predicted = previousIdleSeconds!.Value + Math.Max(0, prefixSeconds);
        boundaryIdleSeconds = predicted;
        return timeoutSeconds <= 0 || predicted < timeoutSeconds;
    }

    private static bool Valid(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;
}
