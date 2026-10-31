using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ScreenTime;

public static class VisibilitySelfTests
{
    public static string Run()
    {
        int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Visibility test: " + name); checks++; }
        var geometry = new VisibilityGeometry(new[] { new WindowRect(0, 0, 1000, 1000), new WindowRect(1000, 0, 2000, 1000) });
        Check(geometry.VisibleArea(new(0, 0, 2000, 1000)) == 2000000, "two monitors form one display union");
        geometry.Cover(new(0, 0, 1000, 1000));
        Check(geometry.VisibleArea(new(0, 0, 1000, 1000)) == 0, "fully covered window has no exposure");
        Check(geometry.VisibleArea(new(500, 0, 1500, 1000)) == 500000, "partial occlusion across monitors");
        geometry.Cover(new(1200, 200, 1800, 800));
        Check(geometry.VisibleArea(new(1000, 0, 2000, 1000)) == 640000, "hole subtraction preserves remaining area");
        var clones = new VisibilityGeometry(new[] { new WindowRect(0, 0, 100, 100), new WindowRect(0, 0, 100, 100) });
        Check(clones.VisibleArea(new(0, 0, 100, 100)) == 10000, "mirrored displays do not double count");
        string root = Path.Combine(AppContext.BaseDirectory, "self-test-data", "ScreenTime-visibility-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UsageStore(root);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(store.TimeZoneId);
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
            var start = new DateTimeOffset(today.AddHours(10), zone.GetUtcOffset(today.AddHours(10)));
            var apps = new[] { new VisibleApp("a", "A"), new VisibleApp("a", "Second A window"), new VisibleApp("b", "B"), new VisibleApp(UsageRules.SelfAppId, "self") };
            var interval = new VisibleInterval(start, start.AddMinutes(10), 600, apps);
            store.Add(new UsageSegment { Start = start, End = start.AddMinutes(10), Seconds = 600, Kind = "app", AppId = "a", AppName = "A", ActivityTracked = true });
            store.AddVisible(interval); store.AddVisible(interval);
            var day = store.GetDay(today);
            Check(day.ScreenSeconds == 600 && day.ForegroundSeconds == 600, "visibility does not inflate existing screen or active metrics");
            Check(day.Apps.Single(a => a.AppId == "a").VisibleSeconds == 600, "same-app windows and queue retry are deduplicated");
            Check(day.Apps.Single(a => a.AppId == "b").VisibleSeconds == 600, "nonfocused app accumulates visibility");
            Check(day.VisibleSeconds == 1200 && day.VisibilityObservedSeconds == 600, "parallel sum differs from unique coverage");
            Check(day.Apps.All(a => a.AppId != UsageRules.SelfAppId), "self excluded");
            Check(day.VisibleHourly.Sum() == 1200 && day.VisibilityObservedHourly.Sum() == 600, "hour aggregates consistent");
            day.AppVisibleHourly["a"][10] = -9;
            Check(store.GetDay(today).AppVisibleHourly["a"][10] == 600, "returned arrays do not mutate persistence");
            store.AddVisible(new(start.AddMinutes(5), start.AddMinutes(15), 600, new[] { apps[0] }));
            day = store.GetDay(today);
            Check(day.Apps.Single(a => a.AppId == "a").VisibleSeconds == 900 && day.VisibilityObservedSeconds == 900, "partially overlapping retry only adds uncovered suffix");
            var midnight = start.Date.AddDays(1);
            var cross = new DateTimeOffset(midnight.AddSeconds(-30), zone.GetUtcOffset(midnight.AddSeconds(-30)));
            store.AddVisible(new(cross, cross.AddMinutes(1), 60, new[] { apps[0] }));
            Check(store.GetDay(today).Apps.Single(a => a.AppId == "a").VisibleSeconds == 930, "cross-midnight prefix");
            Check(store.GetDay(today.AddDays(1)).Apps.Single(a => a.AppId == "a").VisibleSeconds == 30, "cross-midnight suffix");
            store.Save();
            var reload = new UsageStore(root);
            Check(reload.GetDay(today).VisibleSeconds == 1530, "compact aggregates survive reload");
            string csv = Path.Combine(root, "export.csv"); reload.ExportCsv(csv, today, today);
            Check(File.ReadAllText(csv).Contains("app_visible") && File.ReadAllText(csv).Contains("visibility_coverage"), "CSV separates visibility from device time");
            Check(reload.GetDay(today.AddDays(-1)).VisibilityObservedSeconds == 0, "old days have no invented visibility");
            reload.DeleteRange(today, today);
            Check(reload.GetDay(today).VisibilityObservedSeconds == 0 && !File.Exists(Path.Combine(root, "visibility", today.ToString("yyyy-MM-dd") + ".json")), "delete range removes sidecar");
            reload.DeleteAll();
            Check(!Directory.EnumerateFiles(Path.Combine(root, "visibility"), "*.json").Any(), "delete all removes visible aggregates");
            string sidecars = Path.Combine(root, "visibility");
            string orphan = Path.Combine(sidecars, today.ToString("yyyy-MM-dd") + ".json.tmp");
            File.WriteAllText(orphan, "unfinished write");
            reload.DeleteAll();
            Check(!File.Exists(orphan), "delete all removes failed atomic write remnants");
            for (int i = 1; i <= 12; i++)
            {
                var past = start.AddDays(-i);
                reload.AddVisible(new(past, past.AddSeconds(10), 10, new[] { apps[0] }));
            }
            Check(reload.CachedVisibilityDays <= 8, "visibility cache independently stays within eight dates");
            reload.TrimCache(1);
            Check(reload.CachedVisibilityDays <= 1, "closing UI can release historical visibility aggregates");
            Check(reload.GetDay(today.AddDays(-12)).VisibleSeconds == 10, "eviction saves dirty aggregates before releasing them");
            var archived = start.AddDays(-91);
            reload.AddVisible(new(archived, archived.AddMinutes(1), 60, new[] { apps[0] })); reload.Save(); reload.Prune();
            var archivedSummary = reload.GetDay(archived.Date);
            Check(archivedSummary.VisibleSeconds == 60 && archivedSummary.AppVisibleHourly.Count == 0, "90-day retention keeps totals without hourly detail");
            var expired = start.AddDays(-365);
            reload.AddVisible(new(expired, expired.AddSeconds(5), 5, new[] { apps[0] })); reload.Save(); reload.Prune();
            Check(reload.GetDay(expired.Date).VisibilityObservedSeconds == 0, "365-day retention expires visibility totals");
            reload.DeleteAll();
            var failedStore = new VisibleUsageStore(Path.Combine(root, "failed-visible"), zone);
            failedStore.Add(interval);
            Directory.CreateDirectory(Path.Combine(root, "failed-visible", today.ToString("yyyy-MM-dd") + ".json.tmp"));
            bool denied = false;
            try { failedStore.Save(); } catch (IOException) { denied = true; } catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "atomic save failures surface");
            Directory.Delete(Path.Combine(root, "failed-visible", today.ToString("yyyy-MM-dd") + ".json.tmp"));
            failedStore.Add(interval); failedStore.Save();
            var recovered = new DaySummary(); new VisibleUsageStore(Path.Combine(root, "failed-visible"), zone).Apply(recovered, today);
            Check(recovered.VisibleSeconds == 1200 && recovered.VisibilityObservedSeconds == 600, "retry after write failure preserves totals without duplicating the interval");
        }
        finally { Directory.Delete(root, true); }
        return "Visibility tests passed: " + checks + " assertions (occlusion, parallel/same-app union, overlap retry, midnight, persistence, CSV, deletion).";
    }
}
