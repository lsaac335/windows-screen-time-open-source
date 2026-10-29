using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ScreenTime;

public static class CoreSelfTests
{
    public static string Run()
    {
        string testParent = Path.Combine(AppContext.BaseDirectory, "self-test-data");
        string root = Path.Combine(testParent, "ScreenTime-CoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("Core self-test failed: " + name + " (test data: " + root + ")");
            checks++;
        }
        bool Near(double a, double b) => Math.Abs(a - b) < 0.001;
        DateTime today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Date;
        DateTimeOffset At(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)), TimeSpan.Zero);
        var store = new UsageStore(Path.Combine(root, "records"));
        DateTimeOffset start = At(today.AddHours(10));
        void Add(string kind, string id, string name, DateTimeOffset time, double duration, double? actual = null) => store.Add(new UsageSegment
        {
            Kind = kind, AppId = id, AppName = name, Start = time, End = time.AddSeconds(duration), Seconds = actual ?? duration
        });
        Add("app", "editor", "编辑器", start, 600);
        Add("app", "browser", "浏览器", start.AddSeconds(600), 300);
        Add("app", "editor", "编辑器", start.AddSeconds(900), 60);
        Add("locked", "", "", start.AddSeconds(960), 120);
        Add("screenoff", "", "", start.AddSeconds(1080), 180);
        Add("paused", "", "", start.AddSeconds(1260), 60);
        Add("gap", "", "", start.AddSeconds(1320), 30);
        Add("unknown", "", "", start.AddSeconds(1350), 15);
        var day = store.GetDay(today);
        Check(Near(day.ForegroundSeconds, 960) && Near(day.ScreenSeconds, 1080), "app vs screen totals");
        Check(Near(day.LockedSeconds, 120) && Near(day.ObservedSeconds, 1260) && Near(day.UnknownSeconds, 105), "lock/off/pause/gap distinction");
        Check(day.Apps.Count == 2 && Near(day.Apps.Single(a => a.AppId == "editor").Seconds, 660), "stable app grouping, one foreground owner");
        Check(Near(day.Apps.Sum(a => a.Share), 1) && day.HasData && day.HasGaps, "share denominator and partial coverage");
        Check(Near(day.ForegroundHourly.Sum(), day.ForegroundSeconds) && Near(day.ScreenHourly.Sum(), day.ScreenSeconds), "hour totals match daily totals");
        Check(Near(day.ObservedHourly.Sum(), 1260) && Near(day.UnknownHourly.Sum(), 105), "observed and unknown hours retain separate coverage");
        Check(Near(day.AppHourly["editor"].Sum(), 660) && Near(day.AppHourly["browser"].Sum(), 300), "per-app hourly totals");
        day.ObservedHourly[10] = -1;
        day.UnknownHourly[10] = -1;
        day.AppHourly["editor"][10] = -1;
        day.AppHourly["injected"] = new double[24];
        var copyCheck = store.GetDay(today);
        Check(Near(copyCheck.ObservedHourly[10], 1260) && Near(copyCheck.UnknownHourly[10], 105) &&
              Near(copyCheck.AppHourly["editor"][10], 660) && !copyCheck.AppHourly.ContainsKey("injected"), "all new hourly fields are deep copies");
        Add("app", "duplicate", "另一个屏幕/重复事件", start, 120);
        Check(Near(store.GetDay(today).ForegroundSeconds, 960) && store.GetDay(today).Apps.All(a => a.AppId != "duplicate"), "overlap never double counts another app or display");
        var hourlyStart = At(today.AddDays(-2).AddHours(10).AddMinutes(59).AddSeconds(30));
        Add("app", "hourly", "小时边界", hourlyStart, 90);
        var hourly = store.GetDay(today.AddDays(-2));
        Check(Near(hourly.ForegroundHourly[10], 30) && Near(hourly.ForegroundHourly[11], 60), "split hour boundary proportionally");
        Check(Near(hourly.AppHourly["hourly"][10], 30) && Near(hourly.AppHourly["hourly"][11], 60), "per-app hour boundary split");
        var coverageStart = At(today.AddDays(-2).AddHours(11).AddMinutes(59).AddSeconds(45));
        Add("paused", "", "", coverageStart, 30);
        Add("screenoff", "", "", coverageStart.AddHours(1), 30);
        var coverage = store.GetDay(today.AddDays(-2));
        Check(Near(coverage.UnknownHourly[11], 15) && Near(coverage.UnknownHourly[12], 15) &&
              Near(coverage.ObservedHourly[12], 15) && Near(coverage.ObservedHourly[13], 15) &&
              Near(coverage.ForegroundHourly[12], 0) && Near(coverage.ScreenHourly[12], 0), "unknown vs observed-zero hours across boundaries");

        var cross = At(today.AddDays(-1).AddHours(23).AddMinutes(55));
        Add("app", "reader", "阅读器", cross, 600);
        Check(Near(store.GetDay(today.AddDays(-1)).ForegroundSeconds, 300), "midnight first day");
        Check(Near(store.GetDay(today).Apps.Single(a => a.AppId == "reader").Seconds, 300), "midnight second day");
        Check(store.GetWeek(today).Count == 7 && store.GetWeek(today).Last().Date == today.ToString("yyyy-MM-dd"), "seven-day range");

        Add("app", "normalized", "归一化", start.AddHours(2), 60, 100);
        Check(Near(store.GetDay(today).Apps.Single(a => a.AppId == "normalized").Seconds, 60), "reject inflated duration");
        Add("app", "scaled", "比例", start.AddHours(2).AddMinutes(5), 120, 60);
        Check(Near(store.GetDay(today).Apps.Single(a => a.AppId == "scaled").Seconds, 60), "monotonic proportional duration");
        Add("app", "formula", "  =HYPERLINK(\"http://example.invalid\")", start.AddHours(3), 30);
        string csvPath = Path.Combine(root, "export.csv");
        store.ExportCsv(csvPath, today, today.AddDays(1));
        string csv = File.ReadAllText(csvPath);
        Check(csv.Contains("'  =HYPERLINK") && csv.Contains(",30,2"), "CSV formula safety and invariant numeric seconds");
        Check(csv.Contains("\"coverage\",\"无有效记录\",,2"), "unobserved day is not a false zero");
        store.Save();
        var reloaded = new UsageStore(store.DataDirectory);
        Check(Near(reloaded.GetDay(today).ForegroundSeconds, store.GetDay(today).ForegroundSeconds), "save and reload");
        reloaded.DeleteRange(today.AddDays(-1), today.AddDays(-1));
        reloaded.Save();
        Check(!new UsageStore(store.DataDirectory).GetDay(today.AddDays(-1)).HasData, "delete date survives reload");
        reloaded.DeleteAll();
        reloaded.Save();
        Check(!new UsageStore(store.DataDirectory).GetDay(today).HasData, "delete all survives reload");

        var retention = new UsageStore(Path.Combine(root, "retention"));
        void Old(int age) => retention.Add(new UsageSegment { Kind = "app", AppId = "old", AppName = "历史", Start = At(today.AddDays(-age).AddHours(8)), End = At(today.AddDays(-age).AddHours(8).AddMinutes(1)), Seconds = 60 });
        Old(89); Old(90); Old(364); Old(365);
        retention.Prune();
        Check(retention.GetDay(today.AddDays(-89)).HasHourlyDetails && retention.GetDay(today.AddDays(-89)).HasData, "90-day recent detail boundary");
        Check(!retention.GetDay(today.AddDays(-90)).HasHourlyDetails && Near(retention.GetDay(today.AddDays(-90)).ForegroundSeconds, 60), "old detail becomes summary");
        var oldSummary = retention.GetDay(today.AddDays(-90));
        Check(oldSummary.AppHourly.Count == 0 && Near(oldSummary.ObservedHourly.Sum(), 0) && Near(oldSummary.UnknownHourly.Sum(), 0), "archived summaries contain no fabricated hourly detail");
        Check(retention.GetDay(today.AddDays(-364)).HasData && !retention.GetDay(today.AddDays(-365)).HasData, "365-day summary retention boundary");
        var archived = new UsageStore(retention.DataDirectory);
        Check(!archived.GetDay(today.AddDays(-90)).HasHourlyDetails && Near(archived.GetDay(today.AddDays(-90)).ForegroundSeconds, 60), "archived summary reload");

        // Self exclusion retains measured screen coverage, rather than turning it into a gap.
        var excluded = new UsageStore(Path.Combine(root, "self-exclusion"));
        DateTime selfDate = today.AddDays(-3);
        DateTimeOffset selfStart = At(selfDate.AddHours(10).AddMinutes(59).AddSeconds(30));
        void SelfAdd(string kind, string id, string name, DateTimeOffset time, double duration) => excluded.Add(new UsageSegment
        {
            Kind = kind, AppId = id, AppName = name, Start = time, End = time.AddSeconds(duration), Seconds = duration
        });
        SelfAdd("app", UsageRules.SelfAppId.ToUpperInvariant(), "屏幕时间", selfStart, 90);
        SelfAdd(UsageRules.ScreenOnlyKind, "ignored-attribution", "不得归属", selfStart.AddSeconds(90), 30);
        var selfOnly = excluded.GetDay(selfDate);
        Check(Near(selfOnly.ScreenSeconds, 120) && Near(selfOnly.ObservedSeconds, 120) && Near(selfOnly.ForegroundSeconds, 0) &&
              Near(selfOnly.UnknownSeconds, 0) && selfOnly.HasData, "self-only use retains known screen coverage with zero foreground");
        Check(selfOnly.Apps.Count == 0 && selfOnly.AppHourly.Count == 0 && Near(selfOnly.ForegroundHourly.Sum(), 0), "self-only use creates no application totals or hours");
        Check(Near(selfOnly.ScreenHourly[10], 30) && Near(selfOnly.ScreenHourly[11], 90) &&
              Near(selfOnly.ObservedHourly.Sum(), 120), "self exclusion preserves cross-hour screen distribution");
        SelfAdd("app", "different-app-same-name", "屏幕时间", selfStart.AddSeconds(120), 60);
        var sameName = excluded.GetDay(selfDate);
        Check(sameName.Apps.Count == 1 && sameName.Apps[0].AppId == "different-app-same-name" &&
              Near(sameName.ForegroundSeconds, 60) && Near(sameName.Apps[0].Share, 1), "same display name with a different identity is not excluded");
        SelfAdd("app", UsageRules.SelfAppId, "屏幕时间", At(today.AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(30)), 90);
        Check(Near(excluded.GetDay(today.AddDays(-1)).ScreenSeconds, 30) && Near(excluded.GetDay(today).ScreenSeconds, 60) &&
              Near(excluded.GetDay(today.AddDays(-1)).ForegroundSeconds + excluded.GetDay(today).ForegroundSeconds, 0), "self exclusion splits midnight without app attribution");
        excluded.Save();
        var excludedReload = new UsageStore(excluded.DataDirectory);
        Check(Near(excludedReload.GetDay(selfDate).ForegroundSeconds, 60) && Near(excludedReload.GetDay(selfDate).ScreenSeconds, 180), "new exclusion rule survives persistence");

        // Seed authentic legacy totals using a temporary identity, then replace that identity in
        // both raw data and derived aggregates to emulate a pre-exclusion daily file.
        const string legacySelfId = "legacy-self-placeholder";
        const string legacySelfName = "ScreenTime Self Sentinel";
        string legacyDirectory = Path.Combine(root, "legacy-self-detail");
        var legacySeed = new UsageStore(legacyDirectory);
        DateTime legacyDate = today.AddDays(-4);
        DateTimeOffset legacyStart = At(legacyDate.AddHours(10).AddMinutes(59).AddSeconds(30));
        void LegacyAdd(string kind, string id, string name, double offset, double seconds) => legacySeed.Add(new UsageSegment
        {
            Kind = kind, AppId = id, AppName = name, Start = legacyStart.AddSeconds(offset),
            End = legacyStart.AddSeconds(offset + seconds), Seconds = seconds
        });
        LegacyAdd("app", legacySelfId, legacySelfName, 0, 90);
        LegacyAdd("app", "legacy-editor", "编辑器", 90, 30);
        LegacyAdd("locked", "", "", 120, 20);
        LegacyAdd("screenoff", "", "", 140, 25);
        LegacyAdd("paused", "", "", 165, 15);
        var legacyBefore = legacySeed.GetDay(legacyDate);
        legacySeed.Save();
        string legacyPath = Path.Combine(legacyDirectory, legacyDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(legacyPath, File.ReadAllText(legacyPath).Replace(legacySelfId, UsageRules.SelfAppId));
        var legacyMigratedStore = new UsageStore(legacyDirectory);
        var legacyAfter = legacyMigratedStore.GetDay(legacyDate);
        Check(Near(legacyAfter.ForegroundSeconds, 30) && legacyAfter.Apps.Count == 1 &&
              legacyAfter.Apps[0].AppId == "legacy-editor" && Near(legacyAfter.Apps[0].Share, 1), "legacy detail migration removes only self foreground and recomputes shares");
        Check(Near(legacyAfter.ScreenSeconds, legacyBefore.ScreenSeconds) && Near(legacyAfter.ObservedSeconds, legacyBefore.ObservedSeconds) &&
              Near(legacyAfter.LockedSeconds, legacyBefore.LockedSeconds) && Near(legacyAfter.UnknownSeconds, legacyBefore.UnknownSeconds) &&
              legacyAfter.FirstRecorded == legacyBefore.FirstRecorded && legacyAfter.LastRecorded == legacyBefore.LastRecorded &&
              legacyAfter.HasGaps == legacyBefore.HasGaps, "legacy detail migration preserves screen, coverage, lock, gaps and bounds");
        Check(Near(legacyAfter.ScreenHourly[10], 30) && Near(legacyAfter.ScreenHourly[11], 110) &&
              Near(legacyAfter.ForegroundHourly[10], 0) && Near(legacyAfter.ForegroundHourly[11], 30) &&
              Near(legacyAfter.Apps.Sum(a => a.Seconds), legacyAfter.ForegroundSeconds), "legacy detail migration updates hourly and daily foreground consistently");
        string migratedDetailText = File.ReadAllText(legacyPath);
        using (var detailJson = JsonDocument.Parse(migratedDetailText))
        {
            var migratedSegment = detailJson.RootElement.GetProperty("Segments")[0];
            Check(migratedSegment.GetProperty("Kind").GetString() == UsageRules.ScreenOnlyKind &&
                  migratedSegment.GetProperty("AppId").GetString() == "" && migratedSegment.GetProperty("AppName").GetString() == "" &&
                  !migratedDetailText.Contains(UsageRules.SelfAppId) && !migratedDetailText.Contains(legacySelfName), "legacy detail conversion is persisted without self attribution");
        }
        var legacyReload = new UsageStore(legacyDirectory);
        Check(Near(legacyReload.GetDay(legacyDate).ForegroundSeconds, 30) && File.ReadAllText(legacyPath) == migratedDetailText,
            "legacy detail migration is idempotent on repeated load");
        string migratedCsvPath = Path.Combine(root, "self-exclusion.csv");
        legacyReload.ExportCsv(migratedCsvPath, legacyDate, legacyDate);
        string migratedCsv = File.ReadAllText(migratedCsvPath);
        Check(!migratedCsv.Contains(legacySelfName) && migratedCsv.Contains("\"foreground\",\"前台使用时长\",30,2") &&
              migratedCsv.Contains("\"screen\",\"亮屏时长\",140,2"), "CSV exports corrected foreground with retained screen total and rule version 2");

        // Archived summaries have no raw intervals left; migration must not fabricate any.
        string archiveDirectory = Path.Combine(root, "legacy-self-archive");
        _ = new UsageStore(archiveDirectory);
        DateTime archiveDate = today.AddDays(-100);
        var archiveFirst = At(archiveDate.AddHours(8));
        var archiveLast = archiveFirst.AddSeconds(1000);
        var archiveSummary = new DaySummary
        {
            Date = archiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ScreenSeconds = 800, ForegroundSeconds = 600, LockedSeconds = 200,
            ObservedSeconds = 900, UnknownSeconds = 100, FirstRecorded = archiveFirst, LastRecorded = archiveLast,
            HasData = true, HasGaps = true, HasHourlyDetails = false,
            Apps = new()
            {
                new AppUsage { AppId = UsageRules.SelfAppId.ToUpperInvariant(), Name = legacySelfName, Seconds = 240, Share = 0.4 },
                new AppUsage { AppId = "archive-editor", Name = "编辑器", Seconds = 360, Share = 0.6 }
            }
        };
        string archivePath = Path.Combine(archiveDirectory, archiveSummary.Date + ".json");
        File.WriteAllText(archivePath, JsonSerializer.Serialize(new { Version = 1, Date = archiveSummary.Date, Segments = (UsageSegment[]?)null, Summary = archiveSummary }));
        var migratedArchiveStore = new UsageStore(archiveDirectory);
        var migratedArchive = migratedArchiveStore.GetDay(archiveDate);
        Check(Near(migratedArchive.ForegroundSeconds, 360) && migratedArchive.Apps.Count == 1 &&
              migratedArchive.Apps[0].AppId == "archive-editor" && Near(migratedArchive.Apps[0].Share, 1) &&
              Near(migratedArchive.Apps.Sum(a => a.Seconds), migratedArchive.ForegroundSeconds), "archived self migration corrects foreground, apps and shares");
        Check(Near(migratedArchive.ScreenSeconds, 800) && Near(migratedArchive.LockedSeconds, 200) &&
              Near(migratedArchive.ObservedSeconds, 900) && Near(migratedArchive.UnknownSeconds, 100) &&
              migratedArchive.FirstRecorded == archiveFirst && migratedArchive.LastRecorded == archiveLast && migratedArchive.HasData && migratedArchive.HasGaps,
            "archived self migration preserves screen, coverage and historical bounds");
        Check(!migratedArchive.HasHourlyDetails && migratedArchive.AppHourly.Count == 0 &&
              Near(migratedArchive.ForegroundHourly.Sum() + migratedArchive.ScreenHourly.Sum(), 0), "archived self migration does not invent hourly detail");
        string migratedArchiveText = File.ReadAllText(archivePath);
        using (var archiveJson = JsonDocument.Parse(migratedArchiveText))
            Check(archiveJson.RootElement.GetProperty("Segments").ValueKind == JsonValueKind.Null && !migratedArchiveText.Contains(legacySelfName),
                "archived conversion remains summary-only on disk");
        var twiceMigratedArchive = new UsageStore(archiveDirectory);
        Check(Near(twiceMigratedArchive.GetDay(archiveDate).ForegroundSeconds, 360) && File.ReadAllText(archivePath) == migratedArchiveText,
            "archived self migration does not subtract twice");

        string orphanAllDirectory = Path.Combine(root, "orphan-delete-all");
        _ = new UsageStore(orphanAllDirectory);
        string orphanAllPath = Path.Combine(orphanAllDirectory, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json.tmp");
        File.WriteAllText(orphanAllPath, "private orphan record");
        var orphanReload = new UsageStore(orphanAllDirectory);
        bool orphanWasNotRecovered = !orphanReload.GetDay(today).HasData && File.Exists(orphanAllPath);
        orphanReload.DeleteAll();
        Check(orphanWasNotRecovered && !File.Exists(orphanAllPath) && File.Exists(Path.Combine(orphanAllDirectory, "store-meta.json")), "restarted store deletes orphan temporary record without recovering or deleting metadata");

        string orphanRangeDirectory = Path.Combine(root, "orphan-delete-range");
        var orphanRange = new UsageStore(orphanRangeDirectory);
        string inRangePath = Path.Combine(orphanRangeDirectory, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json.tmp");
        string outOfRangePath = Path.Combine(orphanRangeDirectory, today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json.tmp");
        string invalidNamePath = Path.Combine(orphanRangeDirectory, "2026-99-99.json.tmp");
        string runtimePath = Path.Combine(orphanRangeDirectory, "runtime.json.tmp");
        File.WriteAllText(inRangePath, "private in-range record");
        File.WriteAllText(outOfRangePath, "private out-of-range record");
        File.WriteAllText(invalidNamePath, "not a valid daily file");
        File.WriteAllText(runtimePath, "runtime metadata");
        orphanRange.DeleteRange(today, today);
        Check(!File.Exists(inRangePath) && File.Exists(outOfRangePath) && File.Exists(invalidNamePath) && File.Exists(runtimePath), "temporary range deletion touches only valid in-range daily files");

        string orphanPruneDirectory = Path.Combine(root, "orphan-prune");
        var orphanPrune = new UsageStore(orphanPruneDirectory);
        string recentTemporary = Path.Combine(orphanPruneDirectory, today.AddDays(-89).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json.tmp");
        string expiredTemporary = Path.Combine(orphanPruneDirectory, today.AddDays(-90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json.tmp");
        string longExpiredTemporary = Path.Combine(orphanPruneDirectory, today.AddDays(-365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json.tmp");
        File.WriteAllText(recentTemporary, "recent private detail");
        File.WriteAllText(expiredTemporary, "expired private detail");
        File.WriteAllText(longExpiredTemporary, "long-expired private detail");
        orphanPrune.Prune();
        Check(File.Exists(recentTemporary) && !File.Exists(expiredTemporary) && !File.Exists(longExpiredTemporary) && File.Exists(Path.Combine(orphanPruneDirectory, "store-meta.json")), "orphan detail expiry honors the 90-day boundary without deleting metadata");

        string corrupted = Path.Combine(root, "corrupt");
        _ = new UsageStore(corrupted);
        File.WriteAllText(Path.Combine(corrupted, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json"), "{invalid");
        bool failedSafely = false;
        try { _ = new UsageStore(corrupted); } catch (InvalidDataException) { failedSafely = true; }
        Check(failedSafely && File.Exists(Path.Combine(corrupted, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json")), "corrupt data preserved, not silently reset");

        // The unique, explicitly-owned temporary directory is the only recursive cleanup target.
        string resolved = Path.GetFullPath(root);
        string temporary = Path.GetFullPath(testParent) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("ScreenTime-CoreTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected self-test cleanup path.");
        Directory.Delete(resolved, true);
        return "Core self-tests passed: " + checks + " assertions (aggregation, midnight, unknown intervals, CSV, self exclusion and migration, persistence, deletion, retention, corrupt-file safety).";
    }
}
