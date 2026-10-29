using System;
using System.IO;
using System.Linq;

namespace ScreenTime;

public static class IdleFilterSelfTests
{
    public static string Run()
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("Idle filter self-test failed: " + name);
            checks++;
        }
        bool Near(double a, double b) => Math.Abs(a - b) < .00001;
        var start = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        UsageSegment Segment(string kind = "app") => new()
        {
            Start = start, End = start.AddSeconds(1), Seconds = 1,
            Kind = kind, AppId = kind == "app" ? "reader" : "", AppName = kind == "app" ? "阅读器" : ""
        };
        var active = IdleTimeFilter.Split(Segment(), true, 100, 101).ToArray();
        Check(active.Length == 1 && active[0].Kind == "app" && Near(active[0].Seconds, 1), "no-input grace period counts normally");
        var boundary = IdleTimeFilter.Split(Segment(), true, 299.5, 300.5).ToArray();
        Check(boundary.Length == 2 && boundary[0].Kind == "app" && Near(boundary[0].Seconds, .5) && boundary[1].Kind == UsageRules.ScreenOnlyKind,
            "a tick crossing five minutes splits at the exact threshold");
        Check(boundary[0].End == boundary[1].Start && boundary[1].End == start.AddSeconds(1), "split boundaries are contiguous");
        var idle = IdleTimeFilter.Split(Segment(), true, 600, 601).ToArray();
        Check(idle.Length == 1 && idle[0].Kind == UsageRules.ScreenOnlyKind && idle[0].AppId == "" && idle[0].AppName == "", "idle carries no app attribution");
        var resumed = IdleTimeFilter.Split(Segment(), true, 600, .2).ToArray();
        Check(resumed.Length == 2 && resumed[0].Kind == UsageRules.ScreenOnlyKind && Near(resumed[0].Seconds, .8) && resumed[1].Kind == "app" && Near(resumed[1].Seconds, .2),
            "input resumes counting without backfilling the idle prefix");
        var crossedAndResumed = IdleTimeFilter.Split(Segment(), true, 299.5, .2).ToArray();
        Check(crossedAndResumed.Length == 3 && Near(crossedAndResumed[0].Seconds, .5) && Near(crossedAndResumed[1].Seconds, .3) && Near(crossedAndResumed[2].Seconds, .2),
            "threshold then input within the same tick preserves all three intervals");
        Check(IdleTimeFilter.Split(Segment(), true, 299, 300).Single().Kind == "app", "the first full five minutes remain counted");
        Check(IdleTimeFilter.Split(Segment(), true, 300, 301).Single().Kind == UsageRules.ScreenOnlyKind, "time after the threshold is excluded");
        Check(IdleTimeFilter.Split(Segment(), false, 600, 601).Single().Kind == "app", "disabling the filter restores foreground counting");
        foreach (string kind in new[] { "gap", "paused", "unknown", "locked", "screenoff", UsageRules.ScreenOnlyKind })
            Check(IdleTimeFilter.Split(Segment(kind), true, 600, 601).Single().Kind == kind, "filter preserves " + kind);
        foreach (double? missing in new double?[] { null, double.NaN, -1 })
            Check(IdleTimeFilter.Split(Segment(), true, 600, missing).Single().Kind == "app", "invalid idle samples do not erase foreground usage");
        foreach (var parts in new[] { active, boundary, idle, resumed, crossedAndResumed })
            Check(Near(parts.Sum(p => p.Seconds), 1) && parts.All(p => p.End > p.Start) && parts.First().Start == start && parts.Last().End == start.AddSeconds(1),
                "filter conserves duration and full interval coverage");

        string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "self-test-data"));
        string root = Path.Combine(parent, "ScreenTime-IdleTests-" + Guid.NewGuid().ToString("N"));
        var store = new UsageStore(root);
        foreach (var part in boundary) store.Add(part);
        store.Save();
        var day = store.GetDay(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(start, store.TimeZoneId).Date);
        Check(Near(day.ScreenSeconds, 1) && Near(day.ForegroundSeconds, .5) && Near(day.ObservedSeconds, 1), "storage retains all screen-on/observed time while excluding idle app time");
        Check(day.Apps.Count == 1 && Near(day.Apps[0].Seconds, .5) && Near(day.ForegroundHourly.Sum(), .5) && Near(day.ScreenHourly.Sum(), 1), "rankings and hourly statistics use the same filtered duration");
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("ScreenTime-IdleTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected idle-test cleanup path.");
        Directory.Delete(resolved, true);
        return "Idle filter self-tests passed: " + checks + " assertions (five-minute boundary, resume, metric conservation, non-app states, invalid samples, aggregation).";
    }
}
