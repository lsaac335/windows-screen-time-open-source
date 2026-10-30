using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ScreenTime;

/// <summary>Desktop integration checks. No UsageStore or user settings are opened.</summary>
public static class TrackingServiceSelfTests
{
    public static string Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Sampler self-test failed: " + name);
            checks++;
        }
        var parts = new List<UsageSegment>();
        using var service = new TrackingService(true, 5, new Dictionary<string, int>(), paused: true);
        Check(service.SamplerThreadId != Environment.CurrentManagedThreadId && service.SamplerThreadId > 0,
            "native sampling is owned by a different thread");
        // Deliberately stop this (UI) thread longer than the old 3.5-second gap threshold.
        // The sampler must keep its own message pump and split the interval normally.
        Thread.Sleep(4500);
        Check(service.SampleCount >= 3, "sampling continues while the UI thread cannot pump messages");
        service.Flush();
        int before = service.PendingSegments;
        bool failureObserved = false;
        try { service.DrainSegments(_ => throw new InvalidOperationException("simulated persistence failure")); }
        catch (InvalidOperationException) { failureObserved = true; }
        Check(failureObserved && service.PendingSegments >= before, "failed consumer leaves the segment available for retry");
        service.DrainSegments(parts.Add);
        Check(parts.Count >= 4 && parts.All(p => p.Kind == "paused" && p.Seconds <= 3.5),
            "UI stalls do not turn independent sampler intervals into gaps");
        Check(parts.Sum(p => p.Seconds) >= 4.2, "elapsed time survives UI stalls and queue delivery");
        Check(parts.Zip(parts.Skip(1), (a, b) => b.Start >= a.End).All(v => v), "segments never overlap");
        Check(parts.All(p => p.ActivityTracked), "new intervals retain metric schema provenance");
        int delivered = parts.Count;
        service.DrainSegments(parts.Add);
        Check(parts.Count == delivered, "draining a queue does not replay delivered data");
        service.SetIdleTimeoutMinutes(1);
        service.SetAppIdleTimeoutMinutes("test-reader", 0);
        service.SetAppIdleTimeoutMinutes("test-reader", null);
        service.SetIdleFilter(false);
        service.SetPaused(false);
        service.SetPaused(true);
        Check(service.StatusText == "已暂停", "control commands are executed on the sampler thread");
        bool rejected = false;
        try { service.SetIdleTimeoutMinutes(-1); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && service.StatusText == "已暂停", "invalid commands surface without stopping sampling");
        service.Dispose();
        service.DrainSegments(parts.Add);
        Check(parts.Count > delivered, "shutdown flush remains available to the store after the worker stops");
        return "Sampler integration tests passed: " + checks + " assertions (independent STA sampling, blocked UI, queue retry, command dispatch, shutdown flush).";
    }
}
