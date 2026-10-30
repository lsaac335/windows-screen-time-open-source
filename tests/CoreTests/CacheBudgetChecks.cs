using System;
using System.IO;
using ScreenTime;

internal static class CacheBudgetChecks
{
    public static string Run()
    {
        string parent = Path.Combine(AppContext.BaseDirectory, "cache-budget-checks");
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        DateTime today = DateTime.Today;
        UsageSegment Part(int age, string name = "app")
        {
            var start = new DateTimeOffset(today.AddDays(-age).AddHours(8));
            return new UsageSegment { Start = start, End = start.AddSeconds(1), Seconds = 1,
                Kind = "app", AppId = "app", AppName = name, ActivityTracked = true };
        }
        try
        {
            var store = new UsageStore(Path.Combine(root, "budget"));
            store.Add(Part(0));
            store.Add(Part(1, new string('x', 5500000)));
            Check(store.CachedDayCount == 1, "detail byte budget should evict oversized historical data, keeping today");
            Check(store.GetDay(today.AddDays(-1)).ForegroundSeconds == 1 && store.CachedDayCount == 1,
                "budget eviction must preserve and reload the historical total");
            var failure = new UsageStore(Path.Combine(root, "failed-save"));
            failure.Add(Part(0));
            for (int age = 1; age < 8; age++) failure.Add(Part(age));
            string blockedPath = Path.Combine(failure.DataDirectory, today.AddDays(-1).ToString("yyyy-MM-dd") + ".json");
            Directory.CreateDirectory(blockedPath);
            bool denied = false;
            try { failure.Add(Part(8)); } catch (IOException) { denied = true; }
            Check(denied && failure.CachedDayCount == 9, "failed eviction write must preserve dirty memory and surface failure");
            Directory.Delete(blockedPath);
            failure.Save();
            var reloaded = new UsageStore(failure.DataDirectory);
            for (int age = 0; age < 9; age++) Check(reloaded.GetDay(today.AddDays(-age)).ForegroundSeconds == 1,
                "dirty eviction retry must preserve all dates without duplicating time: " + age);
            return "Cache budget/failure checks passed: " + checks + " assertions.";
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test cleanup scope.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
