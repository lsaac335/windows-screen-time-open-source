using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ScreenTime;

/// <summary>Independent, compact hourly aggregates; never duplicates the foreground timeline.</summary>
public sealed class VisibleUsageStore
{
    private readonly string _directory;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, VisibleDay> _cache = new();
    private readonly HashSet<string> _dirty = new();
    private readonly Dictionary<string, long> _access = new();
    private long _clock;
    public int CachedDays => _cache.Count;
    public sealed class VisibleDay
    {
        public int Version { get; set; } = 1;
        public string Date { get; set; } = "";
        public DateTimeOffset? First { get; set; }
        public DateTimeOffset? End { get; set; }
        public double Observed { get; set; }
        public double[]? Hours { get; set; } = new double[24];
        public Dictionary<string, VisibleTotals> Apps { get; set; } = new();
    }
    public sealed class VisibleTotals
    {
        public string Name { get; set; } = "";
        public double Seconds { get; set; }
        public double[]? Hours { get; set; } = new double[24];
    }
    public VisibleUsageStore(string directory, TimeZoneInfo zone)
    { _directory = directory; _zone = zone; }
    private string PathFor(string key) => Path.Combine(_directory, key + ".json");
    private DateTime Today => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone).Date;
    private DateTimeOffset Boundary(DateTime date) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), _zone);
    private VisibleDay? Load(string key, bool create = false)
    {
        if (!_cache.TryGetValue(key, out var day))
        {
            var path = PathFor(key);
            if (!File.Exists(path) && !create) return null;
            day = File.Exists(path) ? JsonSerializer.Deserialize<VisibleDay>(File.ReadAllText(path)) : new VisibleDay { Date = key };
            bool Good(double s) => double.IsFinite(s) && s >= 0;
            bool Hours(double[]? h, double total) => h == null || (h.Length == 24 && h.All(Good) && Math.Abs(h.Sum() - total) < .01);
            if (day == null || day.Version != 1 || day.Date != key || !Good(day.Observed) || !Hours(day.Hours, day.Observed) ||
                day.Apps == null || day.Apps.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null || p.Value.Name == null ||
                    !Good(p.Value.Seconds) || p.Value.Seconds > day.Observed + .01 || !Hours(p.Value.Hours, p.Value.Seconds)) ||
                (day.Observed > 0 && (!day.First.HasValue || !day.End.HasValue || day.End <= day.First)))
                throw new InvalidDataException("可见时间记录损坏，原文件已保留：" + key);
            _cache[key] = day;
        }
        _access[key] = ++_clock;
        return day;
    }
    public void Add(VisibleInterval interval)
    {
        double span = (interval.End - interval.Start).TotalSeconds;
        if (!double.IsFinite(interval.Seconds) || interval.Seconds < 0 || interval.Apps == null)
            throw new ArgumentException("可见时间片段无效。");
        if (span <= 0 || interval.Seconds == 0) return;
        double rate = Math.Min(span, interval.Seconds) / span;
        // Same-app windows collapse to one identity before any accumulation.
        var apps = interval.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Id) && a.Id != UsageRules.SelfAppId)
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var cursor = interval.Start; cursor < interval.End;)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, _zone);
            string key = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var end = Boundary(local.Date.AddDays(1));
            if (end > interval.End) end = interval.End;
            var day = Load(key, true)!;
            var start = day.End.HasValue && day.End > cursor ? day.End.Value : cursor;
            if (start < end)
            {
                double seconds = (end - start).TotalSeconds * rate;
                day.First ??= start;
                day.Observed += seconds;
                AddHours(day.Hours, start, end, rate);
                foreach (var app in apps)
                {
                    if (!day.Apps.TryGetValue(app.Id, out var total)) day.Apps[app.Id] = total = new VisibleTotals { Name = app.Name, Hours = day.Hours == null ? null : new double[24] };
                    total.Name = app.Name; total.Seconds += seconds;
                    AddHours(total.Hours, start, end, rate);
                }
                day.End = end;
                _dirty.Add(key);
            }
            cursor = end;
        }
        Trim();
    }
    private void AddHours(double[]? hours, DateTimeOffset start, DateTimeOffset end, double rate)
    {
        if (hours == null) return;
        for (var cursor = start; cursor < end;)
        {
            var next = cursor.AddMinutes(1).AddSeconds(-cursor.Second).AddTicks(-(cursor.Ticks % TimeSpan.TicksPerSecond));
            if (next > end) next = end;
            hours[TimeZoneInfo.ConvertTime(cursor, _zone).Hour] += (next - cursor).TotalSeconds * rate;
            cursor = next;
        }
    }
    public void Apply(DaySummary summary, DateTime date)
    {
        var day = Load(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (day == null) return;
        summary.VisibilityObservedSeconds = day.Observed;
        summary.VisibilityFirstRecorded = day.First;
        summary.VisibilityObservedHourly = day.Hours?.ToArray() ?? new double[24];
        var apps = summary.Apps.ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in day.Apps)
        {
            if (pair.Key == UsageRules.SelfAppId) continue;
            if (!apps.TryGetValue(pair.Key, out var app)) { app = new AppUsage { AppId = pair.Key, Name = pair.Value.Name }; summary.Apps.Add(app); }
            app.VisibleSeconds = pair.Value.Seconds;
            summary.VisibleSeconds += app.VisibleSeconds;
            if (pair.Value.Hours != null)
            {
                summary.AppVisibleHourly[pair.Key] = pair.Value.Hours.ToArray();
                for (int i = 0; i < 24; i++) summary.VisibleHourly[i] += pair.Value.Hours[i];
            }
        }
        Trim();
    }
    private void SaveDay(string key)
    {
        Directory.CreateDirectory(_directory);
        string path = PathFor(key), temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, _cache[key]); file.Flush(true); }
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        _dirty.Remove(key);
    }
    public void Save() { foreach (var key in _dirty.ToArray()) SaveDay(key); Trim(); }
    public void Trim(int maximumDays = 8)
    {
        string today = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        while (_cache.Count > Math.Max(1, maximumDays))
        {
            var key = _cache.Keys.Where(k => k != today).OrderBy(k => _access[k]).First();
            if (_dirty.Contains(key)) SaveDay(key);
            _cache.Remove(key); _access.Remove(key);
        }
    }
    public void DeleteRange(DateTime start, DateTime end)
    {
        var temporary = Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "????-??-??.json.tmp") : Array.Empty<string>();
        var keys = _cache.Keys.Concat(Files().Concat(temporary).Select(p => Path.GetFileName(p).Substring(0, 10))).Distinct().ToArray();
        foreach (string? key in keys)
            if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date >= start.Date && date <= end.Date)
            {
                File.Delete(PathFor(key!)); File.Delete(PathFor(key!) + ".tmp");
                _cache.Remove(key!); _access.Remove(key!); _dirty.Remove(key!);
            }
    }
    private IEnumerable<string> Files() => Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "????-??-??.json") : Array.Empty<string>();
    public void Prune()
    {
        DeleteRange(DateTime.MinValue, Today.AddDays(-365));
        if (Directory.Exists(_directory))
            foreach (var path in Directory.EnumerateFiles(_directory, "????-??-??.json.tmp"))
                if (DateTime.TryParseExact(Path.GetFileName(path).Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < Today.AddDays(-89)) File.Delete(path);
        foreach (var path in Files().ToArray())
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date >= Today.AddDays(-89)) continue;
            var day = Load(key)!;
            if (day.Hours != null)
            { day.Hours = null; foreach (var app in day.Apps.Values) app.Hours = null; _dirty.Add(key); }
            Trim();
        }
        Save();
    }
}
