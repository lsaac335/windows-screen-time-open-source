using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ScreenTime;

/// <summary>Local-only daily records. All calls are made on the application UI thread.</summary>
public sealed class UsageStore
{
    public string DataDirectory { get; }
    public string TimeZoneId => _zone.Id;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, DayFile> _days = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownDays = new(StringComparer.Ordinal);
    private readonly HashSet<string> _summaryDirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _access = new(StringComparer.Ordinal);
    private long _accessCounter;
    public int CachedDayCount => _days.Count;
    public int SummaryCalculationCount { get; private set; }
    public const int MaximumCachedDays = 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly HashSet<string> KnownKinds = new() { "app", UsageRules.IdleAppKind, UsageRules.ScreenOnlyKind, "locked", "screenoff", "unknown", "paused", "gap" };

    private sealed class StoreMeta
    {
        public int Version { get; set; } = 1;
        public string TimeZoneId { get; set; } = "";
    }

    private sealed class DayFile
    {
        public int Version { get; set; } = 1;
        public string Date { get; set; } = "";
        public List<UsageSegment>? Segments { get; set; } = new();
        public DaySummary Summary { get; set; } = new();
    }

    public UsageStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        var metaPath = Path.Combine(DataDirectory, "store-meta.json");
        if (File.Exists(metaPath))
        {
            var meta = ReadJson<StoreMeta>(metaPath);
            if (meta.Version != 1 || string.IsNullOrWhiteSpace(meta.TimeZoneId))
                throw new InvalidDataException("统计配置版本或时区无效，原始数据已保留。" );
            _zone = TimeZoneInfo.FindSystemTimeZoneById(meta.TimeZoneId);
        }
        else
        {
            _zone = TimeZoneInfo.Local;
            WriteAtomic(metaPath, new StoreMeta { TimeZoneId = _zone.Id });
        }

        foreach (var path in Directory.EnumerateFiles(DataDirectory, "????-??-??.json"))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                continue;
            _knownDays.Add(key);
        }
        Prune();
        string todayKey = Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_knownDays.Contains(todayKey)) LoadDay(todayKey);
        TrimCache(1);
    }

    private DayFile LoadDay(string key)
    {
        if (!_days.TryGetValue(key, out var day))
        {
            string path = DayPath(key);
            day = ReadJson<DayFile>(path);
            if (day.Version is not (1 or 2) || day.Date != key || day.Summary == null)
                throw new InvalidDataException("记录文件结构无效：" + Path.GetFileName(path) + "。原始文件已保留。");
            ValidateDay(day, path);
            if (day.Version == 1 && !File.Exists(path + ".pre-v2")) File.Copy(path, path + ".pre-v2", false);
            bool migrated = MigrateActivityMetrics(day);
            if (ExcludeSelf(day) || migrated) WriteAtomic(path, day);
            // Rebuild once on loading detail rather than trusting an externally edited aggregate.
            if (day.Segments != null) day.Summary = Calculate(day);
            _days.Add(key, day);
        }
        _access[key] = ++_accessCounter;
        return day;
    }

    private void EnsureSummary(string key, DayFile day)
    {
        if (_summaryDirty.Remove(key) && day.Segments != null) day.Summary = Calculate(day);
    }

    private void SaveDay(string key, DayFile day)
    {
        EnsureSummary(key, day);
        WriteAtomic(DayPath(key), day);
        _dirty.Remove(key);
    }

    public void TrimCache(int maximumDays = MaximumCachedDays)
    {
        string today = Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // The byte estimate bounds retained detail, in addition to a fixed date count.
        long Estimate() => _days.Values.Sum(d => 2048L + (d.Segments?.Sum(s => 128L + 2L * (s.AppId.Length + s.AppName.Length)) ?? 0));
        while (_days.Count > Math.Max(1, maximumDays) || (_days.Count > 1 && Estimate() > 10 * 1024 * 1024))
        {
            string? oldest = _days.Keys.Where(k => k != today).OrderBy(k => _access.TryGetValue(k, out long value) ? value : 0).FirstOrDefault();
            if (oldest == null) break;
            if (_dirty.Contains(oldest)) SaveDay(oldest, _days[oldest]);
            _days.Remove(oldest); _access.Remove(oldest); _summaryDirty.Remove(oldest);
        }
    }

    public void Add(UsageSegment segment)
    {
        if (segment == null) throw new ArgumentNullException(nameof(segment));
        double wallSeconds = (segment.End - segment.Start).TotalSeconds;
        if (!double.IsFinite(segment.Seconds) || segment.Seconds < 0)
            throw new ArgumentException("使用片段时长必须是有限的非负数。", nameof(segment));
        if (wallSeconds <= 0 || segment.Seconds == 0) return;
        // A caller supplies monotonic elapsed time. Never inflate beyond this interval's wall span.
        double seconds = Math.Min(segment.Seconds, wallSeconds);
        string kind = segment.Kind ?? "unknown";
        if (!KnownKinds.Contains(kind)) kind = "unknown";
        if (IsAppKind(kind) && IsSelf(segment.AppId)) kind = UsageRules.ScreenOnlyKind;
        DateTimeOffset cursor = segment.Start.ToUniversalTime();
        DateTimeOffset end = segment.End.ToUniversalTime();
        while (cursor < end)
        {
            DateTime local = TimeZoneInfo.ConvertTime(cursor, _zone).DateTime;
            DateTimeOffset boundary = LocalBoundary(local.Date.AddDays(1));
            if (boundary <= cursor) boundary = cursor.AddDays(1);
            DateTimeOffset partEnd = end < boundary ? end : boundary;
            double portion = seconds * ((partEnd - cursor).TotalSeconds / wallSeconds);
            string key = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            DayFile day;
            if (_knownDays.Contains(key)) day = LoadDay(key);
            else
            {
                _knownDays.Add(key);
                _days[key] = day = new DayFile { Date = key, Version = 2 };
                _access[key] = ++_accessCounter;
            }
            if (day.Segments == null)
                throw new InvalidOperationException("不能向已归档为汇总的历史日期写入新片段。" );
            var part = new UsageSegment
            {
                Start = cursor, End = partEnd, Seconds = portion, Kind = kind, ActivityTracked = segment.ActivityTracked, ActivityBasis = segment.ActivityBasis,
                AppId = IsAppKind(kind) ? (string.IsNullOrWhiteSpace(segment.AppId) ? "unknown" : segment.AppId) : "",
                AppName = IsAppKind(kind) ? (string.IsNullOrWhiteSpace(segment.AppName) ? "未识别应用" : segment.AppName) : ""
            };
            AppendUncovered(day, part);
            _dirty.Add(key);
            _summaryDirty.Add(key);
            cursor = partEnd;
        }
        TrimCache();
    }

    public DaySummary GetDay(DateTime date)
    {
        string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_knownDays.Contains(key))
            return new DaySummary { Date = key, HasHourlyDetails = date.Date >= Today().AddDays(-89) };
        var day = LoadDay(key);
        EnsureSummary(key, day);
        // Return a detached value so a UI filter cannot modify persisted aggregate state.
        var result = Clone(day.Summary);
        TrimCache();
        return result;
    }

    public List<DaySummary> GetWeek(DateTime endingDate)
    {
        var result = new List<DaySummary>();
        for (int i = 6; i >= 0; i--) result.Add(GetDay(endingDate.Date.AddDays(-i)));
        return result;
    }

    public void Save()
    {
        foreach (string key in _dirty.ToArray())
        {
            if (!_days.TryGetValue(key, out var day)) continue;
            SaveDay(key, day);
        }
        TrimCache();
    }

    public void ExportCsv(string path, DateTime start, DateTime end)
    {
        if (end.Date < start.Date) throw new ArgumentException("结束日期不能早于开始日期。");
        if ((end.Date - start.Date).TotalDays > 36600) throw new ArgumentException("导出日期范围过大。");
        var text = new StringBuilder();
        text.AppendLine("Date,TimeZone,RangeStart,RangeEnd,HasData,HasGaps,Metric,AppName,Seconds,RuleVersion,ForegroundIsLowerBound");
        for (DateTime date = start.Date; date <= end.Date; date = date.AddDays(1))
        {
            var day = GetDay(date);
            void Row(string metric, string name, double? seconds)
            {
                text.Append(Csv(day.Date)).Append(',').Append(Csv(TimeZoneId)).Append(',')
                    .Append(Csv(day.FirstRecorded?.ToString("O", CultureInfo.InvariantCulture) ?? "")).Append(',')
                    .Append(Csv(day.LastRecorded?.ToString("O", CultureInfo.InvariantCulture) ?? "")).Append(',')
                    .Append(day.HasData ? "true" : "false").Append(',').Append(day.HasGaps ? "true" : "false").Append(',')
                    .Append(Csv(metric)).Append(',').Append(Csv(name)).Append(',')
                    .Append(seconds.HasValue ? seconds.Value.ToString("0.######", CultureInfo.InvariantCulture) : "")
                    .Append(",4,").AppendLine(day.HasLegacyActivity ? "true" : "false");
            }
            if (!day.HasData)
            {
                Row("coverage", "无有效记录", null);
                if (day.UnknownSeconds > 0) Row("unknown", "未记录/状态未知", day.UnknownSeconds);
                continue;
            }
            Row("screen", "亮屏时长", day.ScreenSeconds);
            Row("active", "活跃时长（扣除空闲）", day.ForegroundSeconds);
            Row("foreground", day.HasLegacyActivity ? "前台时长（旧记录仅为下限）" : "前台时长", day.RawForegroundSeconds);
            Row("locked", "锁屏亮屏时长", day.LockedSeconds);
            Row("observed", "已观测时长", day.ObservedSeconds);
            Row("unknown", "未记录/状态未知", day.UnknownSeconds);
            foreach (var app in day.Apps)
            {
                Row("app_active", app.Name, app.Seconds);
                Row("app_foreground", app.Name, app.ForegroundSeconds);
                Row("app_media_inferred", app.Name, app.MediaSeconds);
                Row("app_gamepad", app.Name, app.GamepadSeconds);
            }
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    public void DeleteRange(DateTime start, DateTime end)
    {
        if (end.Date < start.Date) throw new ArgumentException("结束日期不能早于开始日期。");
        foreach (var key in _knownDays.ToArray())
        {
            DateTime day = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (day >= start.Date && day <= end.Date)
            {
                // Delete persisted file first; if denied, preserve in-memory records and surface the failure.
                File.Delete(DayPath(key));
                File.Delete(DayPath(key) + ".tmp");
                File.Delete(DayPath(key) + ".pre-v2");
                _days.Remove(key);
                _knownDays.Remove(key); _access.Remove(key); _summaryDirty.Remove(key);
                _dirty.Remove(key);
            }
        }
        foreach (var temporary in TemporaryDayFiles().Where(f => f.Date >= start.Date && f.Date <= end.Date))
            File.Delete(temporary.Path);
        foreach (var backup in MigrationBackupFiles().Where(f => f.Date >= start.Date && f.Date <= end.Date))
            File.Delete(backup.Path);
    }

    public void DeleteAll()
    {
        foreach (var key in _knownDays.ToArray())
        {
            File.Delete(DayPath(key));
            File.Delete(DayPath(key) + ".tmp");
            File.Delete(DayPath(key) + ".pre-v2");
            _days.Remove(key);
            _knownDays.Remove(key); _access.Remove(key); _summaryDirty.Remove(key);
            _dirty.Remove(key);
        }
        foreach (var temporary in TemporaryDayFiles()) File.Delete(temporary.Path);
        foreach (var backup in MigrationBackupFiles()) File.Delete(backup.Path);
    }

    public void Prune()
    {
        DateTime today = Today();
        foreach (string key in _knownDays.ToArray())
        {
            DateTime date = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date < today.AddDays(-364))
            {
                File.Delete(DayPath(key));
                File.Delete(DayPath(key) + ".tmp");
                _days.Remove(key);
                _knownDays.Remove(key); _access.Remove(key); _summaryDirty.Remove(key);
                _dirty.Remove(key);
            }
            else if (date < today.AddDays(-89))
            {
                var day = LoadDay(key);
                if (day.Segments == null) { TrimCache(); continue; }
                day.Summary = Calculate(day);
                _summaryDirty.Remove(key);
                day.Summary.HasHourlyDetails = false;
                day.Summary.ForegroundHourly = new double[24];
                day.Summary.RawForegroundHourly = new double[24];
                day.Summary.ScreenHourly = new double[24];
                day.Summary.ObservedHourly = new double[24];
                day.Summary.UnknownHourly = new double[24];
                day.Summary.AppHourly = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                day.Summary.AppForegroundHourly = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                day.Segments = null;
                _dirty.Add(key);
            }
        }
        // A failed first atomic write can leave raw detail with no final daily file.
        // Never recover it automatically; expire it with the 90-day detail policy.
        foreach (var temporary in TemporaryDayFiles().Where(f => f.Date < today.AddDays(-89)))
            File.Delete(temporary.Path);
        // The original file can contain raw application intervals, so backups obey
        // the detail retention limit even when the current file is summary-only.
        foreach (var backup in MigrationBackupFiles().Where(f => f.Date < today.AddDays(-89)))
            File.Delete(backup.Path);
        Save();
    }

    private DaySummary Calculate(DayFile day)
    {
        SummaryCalculationCount++;
        var result = new DaySummary { Date = day.Date };
        var apps = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
        var segments = day.Segments ?? new List<UsageSegment>();
        DateTimeOffset? lastEnd = null;
        foreach (var item in segments.OrderBy(s => s.Start))
        {
            bool known = item.Kind is "app" or UsageRules.IdleAppKind or UsageRules.ScreenOnlyKind or "locked" or "screenoff";
            if (!item.ActivityTracked && item.Kind is "app" or UsageRules.ScreenOnlyKind or UsageRules.IdleAppKind)
                result.HasLegacyActivity = true;
            if (lastEnd.HasValue && (item.Start - lastEnd.Value).TotalSeconds > 2) result.HasGaps = true;
            if (!lastEnd.HasValue || item.End > lastEnd) lastEnd = item.End;
            if (!known)
            {
                result.UnknownSeconds += item.Seconds;
                result.HasGaps = true;
                AddHourly(result, item);
                continue;
            }
            result.ObservedSeconds += item.Seconds;
            if (!result.FirstRecorded.HasValue || item.Start < result.FirstRecorded) result.FirstRecorded = item.Start;
            if (!result.LastRecorded.HasValue || item.End > result.LastRecorded) result.LastRecorded = item.End;
            if (item.Kind is "app" or UsageRules.IdleAppKind or UsageRules.ScreenOnlyKind or "locked") result.ScreenSeconds += item.Seconds;
            if (item.Kind == "locked") result.LockedSeconds += item.Seconds;
            if (IsAppKind(item.Kind))
            {
                result.RawForegroundSeconds += item.Seconds;
                if (!apps.TryGetValue(item.AppId, out var app))
                    apps[item.AppId] = app = new AppUsage { AppId = item.AppId, Name = item.AppName };
                app.Name = item.AppName;
                app.ForegroundSeconds += item.Seconds;
                if (item.Kind == "app")
                {
                    result.ForegroundSeconds += item.Seconds;
                    app.Seconds += item.Seconds;
                    if (item.ActivityBasis == "media") app.MediaSeconds += item.Seconds;
                    if (item.ActivityBasis == "gamepad") app.GamepadSeconds += item.Seconds;
                }
            }
            AddHourly(result, item);
        }
        result.HasData = result.ObservedSeconds > 0;
        var date = DateTime.ParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTimeOffset dayStart = LocalBoundary(date);
        DateTimeOffset coverageEnd = date == Today() ? DateTimeOffset.UtcNow : LocalBoundary(date.AddDays(1));
        if (result.HasData && result.ObservedSeconds + 2 < Math.Max(0, (coverageEnd - dayStart).TotalSeconds)) result.HasGaps = true;
        foreach (var app in apps.Values) app.Share = result.ForegroundSeconds > 0 ? app.Seconds / result.ForegroundSeconds : 0;
        result.Apps = apps.Values.OrderByDescending(a => a.Seconds).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();
        return result;
    }

    private void AddHourly(DaySummary summary, UsageSegment item)
    {
        double span = (item.End - item.Start).TotalSeconds;
        DateTimeOffset cursor = item.Start;
        bool observed = item.Kind is "app" or UsageRules.IdleAppKind or UsageRules.ScreenOnlyKind or "locked" or "screenoff";
        double[]? appHourly = null;
        double[]? rawHourly = null;
        if (IsAppKind(item.Kind) && !summary.AppHourly.TryGetValue(item.AppId, out appHourly))
            summary.AppHourly[item.AppId] = appHourly = new double[24];
        if (IsAppKind(item.Kind) && !summary.AppForegroundHourly.TryGetValue(item.AppId, out rawHourly))
            summary.AppForegroundHourly[item.AppId] = rawHourly = new double[24];
        // Minute boundaries handle repeated/skipped local hours and half-hour DST shifts as well.
        while (cursor < item.End)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, _zone);
            DateTimeOffset next = cursor.AddMinutes(1).AddSeconds(-cursor.Second).AddTicks(-(cursor.Ticks % TimeSpan.TicksPerSecond));
            if (next <= cursor) next = cursor.AddMinutes(1);
            if (next > item.End) next = item.End;
            double value = item.Seconds * ((next - cursor).TotalSeconds / span);
            if (observed) summary.ObservedHourly[local.Hour] += value;
            else summary.UnknownHourly[local.Hour] += value;
            if (item.Kind is "app" or UsageRules.IdleAppKind or UsageRules.ScreenOnlyKind or "locked") summary.ScreenHourly[local.Hour] += value;
            if (IsAppKind(item.Kind))
            {
                summary.RawForegroundHourly[local.Hour] += value;
                rawHourly![local.Hour] += value;
            }
            if (item.Kind == "app")
            {
                summary.ForegroundHourly[local.Hour] += value;
                appHourly![local.Hour] += value;
            }
            cursor = next;
        }
    }

    private static void AppendUncovered(DayFile day, UsageSegment part)
    {
        var segments = day.Segments!;
        var uncovered = new List<(DateTimeOffset Start, DateTimeOffset End)> { (part.Start, part.End) };
        // An event callback and a timer can report an interval twice. Existing ownership wins;
        // a second app or another display must never double-count the same wall-clock instant.
        foreach (var existing in segments.Where(s => s.Start < part.End && s.End > part.Start).OrderBy(s => s.Start))
        {
            var remaining = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var range in uncovered)
            {
                if (existing.End <= range.Start || existing.Start >= range.End) remaining.Add(range);
                else
                {
                    if (existing.Start > range.Start) remaining.Add((range.Start, existing.Start));
                    if (existing.End < range.End) remaining.Add((existing.End, range.End));
                }
            }
            uncovered = remaining;
            if (uncovered.Count == 0) break;
        }
        foreach (var range in uncovered)
        {
            var piece = new UsageSegment
            {
                Start = range.Start, End = range.End,
                Seconds = part.Seconds * ((range.End - range.Start).TotalSeconds / (part.End - part.Start).TotalSeconds),
                Kind = part.Kind, AppId = part.AppId, AppName = part.AppName, ActivityTracked = part.ActivityTracked, ActivityBasis = part.ActivityBasis
            };
            // Merge only contiguous, equal-rate intervals. Preserve gaps and proportional durations.
            var last = segments.LastOrDefault();
            if (last != null && last.End == piece.Start && last.Kind == piece.Kind &&
                last.AppId == piece.AppId && last.AppName == piece.AppName && last.ActivityTracked == piece.ActivityTracked && last.ActivityBasis == piece.ActivityBasis &&
                Math.Abs(last.Seconds / (last.End - last.Start).TotalSeconds - piece.Seconds / (piece.End - piece.Start).TotalSeconds) < 0.000001)
            {
                last.End = piece.End;
                last.Seconds += piece.Seconds;
            }
            else segments.Add(piece);
        }
    }

    private static bool IsSelf(string? appId) => string.Equals(appId, UsageRules.SelfAppId, StringComparison.OrdinalIgnoreCase);
    private static bool IsAppKind(string kind) => kind is "app" or UsageRules.IdleAppKind;

    private bool MigrateActivityMetrics(DayFile day)
    {
        if (day.Version != 1) return false;
        if (day.Segments == null)
        {
            // Earlier versions discarded idle ownership. The preserved active totals are
            // only a lower bound for raw foreground time; do not invent the missing time.
            var summary = day.Summary;
            summary.RawForegroundSeconds = summary.ForegroundSeconds;
            summary.RawForegroundHourly = summary.ForegroundHourly.ToArray();
            summary.AppForegroundHourly = summary.AppHourly.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var app in summary.Apps) app.ForegroundSeconds = app.Seconds;
            summary.HasLegacyActivity = summary.HasData || summary.ObservedSeconds > 0 || summary.ForegroundSeconds > 0;
        }
        else day.Summary = Calculate(day);
        day.Version = 2;
        return true;
    }

    private bool ExcludeSelf(DayFile day)
    {
        if (day.Segments != null)
        {
            bool changed = false;
            foreach (var segment in day.Segments)
            {
                if (IsAppKind(segment.Kind) && IsSelf(segment.AppId))
                {
                    // Keep the known screen-on interval and its exact boundaries, but no app attribution.
                    segment.Kind = UsageRules.ScreenOnlyKind;
                    segment.AppId = "";
                    segment.AppName = "";
                    changed = true;
                }
                else if (segment.Kind == UsageRules.ScreenOnlyKind && (segment.AppId.Length > 0 || segment.AppName.Length > 0))
                {
                    segment.AppId = "";
                    segment.AppName = "";
                    changed = true;
                }
            }
            // The summary is derived data while raw detail is present. Repair stale self totals as well.
            changed |= day.Summary.Apps?.Any(a => a != null && IsSelf(a.AppId)) == true ||
                day.Summary.AppHourly?.Keys.Any(IsSelf) == true || day.Summary.AppForegroundHourly?.Keys.Any(IsSelf) == true;
            if (changed) day.Summary = Calculate(day);
            return changed;
        }

        // Archived days no longer have raw intervals. Remove only the self-app contribution,
        // preserving screen/coverage metrics and the historical bounds without inventing detail.
        var summary = day.Summary;
        var selfApps = summary.Apps.Where(a => IsSelf(a.AppId)).ToList();
        var selfHourlyKeys = summary.AppHourly.Keys.Where(IsSelf).ToArray();
        var selfRawHourlyKeys = summary.AppForegroundHourly.Keys.Where(IsSelf).ToArray();
        if (selfApps.Count == 0 && selfHourlyKeys.Length == 0 && selfRawHourlyKeys.Length == 0) return false;
        double removedSeconds = selfApps.Sum(a => a.Seconds);
        summary.ForegroundSeconds = Math.Max(0, summary.ForegroundSeconds - removedSeconds);
        summary.RawForegroundSeconds = Math.Max(0, summary.RawForegroundSeconds - selfApps.Sum(a => a.ForegroundSeconds));
        summary.Apps.RemoveAll(a => IsSelf(a.AppId));
        foreach (string key in selfHourlyKeys)
        {
            double[] selfHours = summary.AppHourly[key];
            for (int hour = 0; hour < Math.Min(summary.ForegroundHourly.Length, selfHours.Length); hour++)
                summary.ForegroundHourly[hour] = Math.Max(0, summary.ForegroundHourly[hour] - selfHours[hour]);
            summary.AppHourly.Remove(key);
        }
        foreach (string key in selfRawHourlyKeys)
        {
            double[] selfHours = summary.AppForegroundHourly[key];
            for (int hour = 0; hour < Math.Min(summary.RawForegroundHourly.Length, selfHours.Length); hour++)
                summary.RawForegroundHourly[hour] = Math.Max(0, summary.RawForegroundHourly[hour] - selfHours[hour]);
            summary.AppForegroundHourly.Remove(key);
        }
        foreach (var app in summary.Apps)
            app.Share = summary.ForegroundSeconds > 0 ? app.Seconds / summary.ForegroundSeconds : 0;
        return true;
    }

    private DateTime Today() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone).Date;

    private DateTimeOffset LocalBoundary(DateTime localDate)
    {
        DateTime value = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (_zone.IsInvalidTime(value)) value = value.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, _zone), TimeSpan.Zero);
    }

    private string DayPath(string key) => Path.Combine(DataDirectory, key + ".json");

    private IEnumerable<(DateTime Date, string Path)> TemporaryDayFiles() => DailyAuxiliaryFiles(".json.tmp");
    private IEnumerable<(DateTime Date, string Path)> MigrationBackupFiles() => DailyAuxiliaryFiles(".json.pre-v2");

    private IEnumerable<(DateTime Date, string Path)> DailyAuxiliaryFiles(string suffix)
    {
        // This scan is deliberately nonrecursive and never targets configuration or icon caches.
        foreach (string path in Directory.EnumerateFiles(DataDirectory, "????-??-??" + suffix, SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string dateText = name.Substring(0, name.Length - suffix.Length);
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                yield return (date, path);
        }
    }

    private static T ReadJson<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                ?? throw new InvalidDataException("内容为空。");
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException("无法读取 " + Path.GetFileName(path) + "，请保留文件并查看错误。", error);
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        string temp = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    private static DaySummary Clone(DaySummary day) => new()
    {
        Date = day.Date, ScreenSeconds = day.ScreenSeconds, ForegroundSeconds = day.ForegroundSeconds,
        RawForegroundSeconds = day.RawForegroundSeconds, HasLegacyActivity = day.HasLegacyActivity,
        LockedSeconds = day.LockedSeconds, ObservedSeconds = day.ObservedSeconds, UnknownSeconds = day.UnknownSeconds,
        FirstRecorded = day.FirstRecorded, LastRecorded = day.LastRecorded, HasData = day.HasData, HasGaps = day.HasGaps,
        HasHourlyDetails = day.HasHourlyDetails,
        ForegroundHourly = day.ForegroundHourly.ToArray(), RawForegroundHourly = day.RawForegroundHourly.ToArray(), ScreenHourly = day.ScreenHourly.ToArray(),
        ObservedHourly = day.ObservedHourly.ToArray(), UnknownHourly = day.UnknownHourly.ToArray(),
        AppHourly = day.AppHourly.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
        AppForegroundHourly = day.AppForegroundHourly.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
        Apps = day.Apps.Select(a => new AppUsage { AppId = a.AppId, Name = a.Name, Seconds = a.Seconds, ForegroundSeconds = a.ForegroundSeconds, Share = a.Share, MediaSeconds = a.MediaSeconds, GamepadSeconds = a.GamepadSeconds }).ToList()
    };

    private static void ValidateDay(DayFile day, string path)
    {
        bool Good(double value) => double.IsFinite(value) && value >= 0;
        bool GoodHours(double[]? values) => values != null && values.Length == 24 && values.All(Good);
        bool GoodMap(Dictionary<string, double[]>? values) => values != null && values.All(p => GoodHours(p.Value));
        if (day.Segments != null && day.Segments.Any(s => s == null || s.End <= s.Start || !Good(s.Seconds) ||
            s.Seconds > (s.End - s.Start).TotalSeconds + .0001 || !KnownKinds.Contains(s.Kind) || s.AppId == null || s.AppName == null))
            throw new InvalidDataException("记录片段损坏：" + Path.GetFileName(path) + "。原始文件已保留。" );
        if (day.Segments == null && (!Good(day.Summary.ScreenSeconds) || !Good(day.Summary.ForegroundSeconds) ||
            !Good(day.Summary.LockedSeconds) || !Good(day.Summary.ObservedSeconds) || !Good(day.Summary.UnknownSeconds) || day.Summary.Apps == null ||
            day.Summary.Apps.Any(a => a == null || !Good(a.Seconds) || !Good(a.MediaSeconds) || !Good(a.GamepadSeconds) ||
                a.MediaSeconds + a.GamepadSeconds > a.Seconds + .0001 || a.AppId == null || a.Name == null) ||
            !GoodHours(day.Summary.ScreenHourly) || !GoodHours(day.Summary.ForegroundHourly) ||
            !GoodHours(day.Summary.ObservedHourly) || !GoodHours(day.Summary.UnknownHourly) || !GoodMap(day.Summary.AppHourly) ||
            (day.Version == 2 && (!Good(day.Summary.RawForegroundSeconds) || day.Summary.RawForegroundSeconds + .000001 < day.Summary.ForegroundSeconds ||
                !GoodHours(day.Summary.RawForegroundHourly) || !GoodMap(day.Summary.AppForegroundHourly) ||
                day.Summary.Apps.Any(a => !Good(a.ForegroundSeconds) || a.ForegroundSeconds + .000001 < a.Seconds) ||
                (!day.Summary.HasLegacyActivity && !CompleteMetricsAreConsistent(day.Summary))))))
            throw new InvalidDataException("历史汇总损坏：" + Path.GetFileName(path) + "。原始文件已保留。" );
    }

    private static bool CompleteMetricsAreConsistent(DaySummary summary)
    {
        // Only complete v2 summaries have this stronger contract. Legacy summaries may
        // lack raw ownership/hour detail; preserve them rather than guessing a repair.
        bool Near(double left, double right) => double.IsFinite(left) && double.IsFinite(right) &&
            Math.Abs(left - right) <= .0001 + Math.Max(left, right) * 1e-9;
        bool Within(double value, double limit) => value <= limit || Near(value, limit);
        if (!Within(summary.ForegroundSeconds, summary.RawForegroundSeconds) ||
            !Within(summary.RawForegroundSeconds + summary.LockedSeconds, summary.ScreenSeconds) ||
            !Within(summary.ScreenSeconds, summary.ObservedSeconds) ||
            !Near(summary.Apps.Sum(a => a.Seconds), summary.ForegroundSeconds) ||
            !Near(summary.Apps.Sum(a => a.ForegroundSeconds), summary.RawForegroundSeconds)) return false;
        if (!summary.HasHourlyDetails) return true;
        if (!Near(summary.ForegroundHourly.Sum(), summary.ForegroundSeconds) ||
            !Near(summary.RawForegroundHourly.Sum(), summary.RawForegroundSeconds) ||
            !Near(summary.ScreenHourly.Sum(), summary.ScreenSeconds) ||
            !Near(summary.ObservedHourly.Sum(), summary.ObservedSeconds) ||
            !Near(summary.UnknownHourly.Sum(), summary.UnknownSeconds)) return false;
        for (int hour = 0; hour < 24; hour++)
            if (!Within(summary.ForegroundHourly[hour], summary.RawForegroundHourly[hour]) ||
                !Within(summary.RawForegroundHourly[hour], summary.ScreenHourly[hour]) ||
                !Within(summary.ScreenHourly[hour], summary.ObservedHourly[hour]) ||
                !Near(summary.AppHourly.Values.Sum(values => values[hour]), summary.ForegroundHourly[hour]) ||
                !Near(summary.AppForegroundHourly.Values.Sum(values => values[hour]), summary.RawForegroundHourly[hour])) return false;
        return true;
    }

    private static string Csv(string value)
    {
        // Quoting alone does not prevent spreadsheet formula execution.
        string trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.StartsWith("\t") || value.StartsWith("\r") || value.StartsWith("\n"))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
