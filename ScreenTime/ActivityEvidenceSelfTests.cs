using System;
using System.Linq;

namespace ScreenTime;

public static class ActivityEvidenceSelfTests
{
    public static string Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Activity evidence: " + name); checks++; }
        bool Near(double a, double b) => Math.Abs(a - b) < .00001;
        var start = DateTimeOffset.UtcNow;
        UsageSegment Segment(string kind = UsageRules.IdleAppKind) => new()
        { Start = start, End = start.AddSeconds(10), Seconds = 10, Kind = kind, AppId = "player", AppName = "播放器", ActivityTracked = true };
        Check(ActivityEvidence.SourceMatches("Browser.exe", @"C:\Apps\browser.exe", null), "exact executable name matches case-insensitively");
        Check(ActivityEvidence.SourceMatches("Family_pub!Player", null, "Family_pub!Player"), "packaged identity matches exactly");
        Check(ActivityEvidence.SourceMatches(@"C:\Apps\player.exe", @"C:\Apps\player.exe", null), "full executable path matches exactly");
        Check(!ActivityEvidence.SourceMatches("Browser", @"C:\Apps\browser.exe", null), "ambiguous friendly name is not inferred");
        Check(!ActivityEvidence.SourceMatches("Other.exe", @"C:\Apps\browser.exe", null), "background music source does not match another foreground app");
        Check(!ActivityEvidence.SourceMatches(@"D:\Other\browser.exe", @"C:\Apps\browser.exe", null), "different full paths are not reduced to a filename");
        Check(!ActivityEvidence.SourceMatches("Family_pub!Other", null, "Family_pub!Player"), "different packaged app is not inferred");
        Check(!ActivityEvidence.SourceMatches("", null, null), "unknown sources stay unknown");
        var media = ActivityEvidence.Supplement(Segment(), true, 0, 10, 300).ToList();
        Check(media.Count == 1 && media[0].Kind == "app" && media[0].ActivityBasis == "media" && Near(media.Sum(s => s.Seconds), 10), "media and gamepad union conserves time");
        var stopped = ActivityEvidence.Supplement(Segment(), false, null, null, 300).ToList();
        Check(stopped.Single().Kind == UsageRules.IdleAppKind, "pause removes media-only activity");
        var expiry = ActivityEvidence.Supplement(Segment(), false, 295, 305, 300).ToList();
        Check(Near(expiry.Where(s => s.Kind == "app").Sum(s => s.Seconds), 5) && Near(expiry.Sum(s => s.Seconds), 10), "controller expiry splits at timeout");
        Check(expiry.First().ActivityBasis == "gamepad" && expiry.Last().ActivityBasis == "idle", "controller basis retained on each side");
        var resumed = ActivityEvidence.Supplement(Segment(), false, 400, 3, 300).ToList();
        Check(Near(resumed.Where(s => s.Kind == "app").Sum(s => s.Seconds), 3), "controller resume does not backfill an idle prefix");
        foreach (string kind in new[] { "app", "locked", "screenoff", "gap", "paused", UsageRules.ScreenOnlyKind })
        {
            var result = ActivityEvidence.Supplement(Segment(kind), true, 0, 0, 300).ToList();
            Check(result.Single().Kind == kind && Near(result.Sum(s => s.Seconds), 10), "non-idle interval is unchanged: " + kind);
        }
        Check(!ActivityEvidence.MeaningfulGamepad(0, 0, 0, 0, 0, 0, 0), "connected resting pad is not input");
        Check(!ActivityEvidence.MeaningfulGamepad(0, 30, 30, 500, -500, 600, -600), "trigger threshold and stick jitter are ignored");
        Check(ActivityEvidence.MeaningfulGamepad(1, 0, 0, 0, 0, 0, 0), "button counts as input");
        Check(ActivityEvidence.MeaningfulGamepad(0, 31, 0, 0, 0, 0, 0), "trigger beyond threshold counts");
        Check(ActivityEvidence.MeaningfulGamepad(0, 0, 0, short.MinValue, 0, 0, 0), "stick extrema do not overflow");
        Check(ActivityEvidence.MeaningfulGamepad(0, 0, 0, 0, 0, 9000, 0), "right stick beyond deadzone counts");
        return "Activity evidence self-tests passed: " + checks + " assertions (source matching, media pause, controller boundaries, union and deadzones).";
    }
}
