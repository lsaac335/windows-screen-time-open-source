using System;

namespace ScreenTime;

/// <summary>Pure event-clock and idle-boundary tests. Does not hook windows or read user input.</summary>
public static class NativeTimingSelfTests
{
    public static string Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native timing self-test failed: " + name);
            checks++;
        }
        static bool Near(double? value, double expected) => value.HasValue && Math.Abs(value.Value - expected) < .000001;
        const long frequency = 1_000_000;
        var clock = new ForegroundEventClock(frequency);
        clock.Reset(10_000, 5_000_000);
        Check(clock.TryMap(10_040, 10_090, 5_090_000, 5_000_000, out long first, out double delay, out _) &&
              first == 5_040_000 && delay == 50, "delayed event uses generation time instead of delivery time");
        Check(clock.TryMap(10_065, 10_090, 5_090_000, first, out long second, out _, out _) &&
              second == 5_065_000 && second - first == 25_000, "queued A to B to C keeps the intermediate interval");
        Check(!clock.TryMap(10_060, 10_091, 5_091_000, second, out _, out _, out string reason) &&
              reason == "out-of-order-event", "out-of-order callbacks cannot change ownership retrospectively");
        Check(clock.TryMap(10_065, 10_092, 5_092_000, second, out long sameTime, out _, out _) && sameTime == second,
              "same-millisecond events preserve sequence without adding time twice");

        clock.Reset(10_000, 5_000_000);
        Check(!clock.TryMap(10_050, 10_130, 5_130_000, 5_100_000, out long rejected, out _, out reason) &&
              reason == "event-before-commit" && rejected == 5_100_000, "poll-committed intervals are never rewritten");
        Check(clock.TryMap(10_095, 10_102, 5_102_000, 5_100_000, out long clamped, out _, out _) && clamped == 5_100_000,
              "clock quantization only clamps forward to the committed boundary");
        Check(!clock.TryMap(10_201, 10_200, 5_200_000, clamped, out _, out _, out reason) && reason == "future-event",
              "future timestamp is rejected");
        Check(!clock.TryMap(10_100, 13_601, 8_601_000, clamped, out _, out _, out reason) && reason == "stale-event",
              "events beyond the observation horizon remain unrecorded");

        clock.Reset(20_000, 9_000_000);
        Check(!clock.TryMap(19_995, 20_010, 9_010_000, 9_000_000, out _, out _, out reason) &&
              reason == "out-of-order-event", "recovering current foreground fences pre-recovery queued events");
        Check(clock.TryMap(20_100, 20_105, 9_105_000, 9_000_000, out long resumed, out _, out _) && resumed == 9_100_000,
              "clock reset establishes a fresh timeline after suspension");

        long wrapAnchor = (long)uint.MaxValue - 20;
        clock.Reset(wrapAnchor, 20_000_000);
        long eventTick = wrapAnchor + 50;
        Check(clock.TryMap(unchecked((uint)eventTick), eventTick + 8, 20_058_000, 20_000_000,
              out long wrapped, out delay, out _) && wrapped == 20_050_000 && delay == 8,
              "32-bit tick wrap maps onto the same monotonic interval");
        long longUptime = 4L * uint.MaxValue + 5_000;
        clock.Reset(longUptime, 30_000_000);
        Check(clock.TryMap(unchecked((uint)(longUptime + 100)), longUptime + 130, 30_130_000, 30_000_000,
              out long oldMachine, out _, out _) && oldMachine == 30_100_000, "multiple tick wraps do not lose uptime high bits");

        clock.Reset(1_000, 1_000_000);
        Check(!clock.TryMap(1_100, 1_110, 1_050_000, 1_000_000, out _, out _, out reason) &&
              reason == "event-clock-drift", "clock mismatch cannot create future usage");

        Check(ForegroundEventClock.TryProjectIdle(290, 295, 4.75, .25, 300, out double? idle) && Near(idle, 294.75),
              "last input preceding the event is projected to its historical boundary");
        Check(ForegroundEventClock.TryProjectIdle(10, .1, .8, .2, 300, out idle) && Near(idle, 10.8),
              "newer input does not invent a reset before a delayed event");
        Check(!ForegroundEventClock.TryProjectIdle(299.8, .1, .4, .2, 300, out _),
              "unknown input crossing the idle threshold is not classified as certain activity");
        Check(ForegroundEventClock.TryProjectIdle(299.8, .1, .4, .2, 600, out idle) && Near(idle, 300.2),
              "application-specific longer threshold keeps an unambiguously active prefix");
        Check(ForegroundEventClock.TryProjectIdle(800, .1, .4, .2, 0, out idle) && Near(idle, 800.4),
              "disabled activity filtering is independent of historical input ambiguity");
        Check(!ForegroundEventClock.TryProjectIdle(null, .1, .4, .2, 300, out _), "missing previous input cannot establish a historical reset");
        Check(!ForegroundEventClock.TryProjectIdle(10, double.NaN, .4, .2, 300, out _), "invalid current input stays unknown");
        Check(!ForegroundEventClock.TryProjectIdle(10, -1, .4, .2, 300, out _), "negative idle sample stays unknown");
        return "Native timing self-tests passed: " + checks + " assertions (event timestamps, queue order, committed boundaries, tick wrap, recovery and idle projection).";
    }
}
