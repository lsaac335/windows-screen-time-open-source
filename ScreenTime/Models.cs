using System;
using System.Collections.Generic;

namespace ScreenTime;

public static class UsageRules
{
    public const string SelfAppId = "screen-time";
    public const string ScreenOnlyKind = "screenon";
    public const string IdleAppKind = "appidle";
}

public class UsageSegment
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public double Seconds { get; set; }
    public string Kind { get; set; } = "unknown";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    // False on older recordings, whose excluded idle time cannot be reconstructed.
    public bool ActivityTracked { get; set; }
    public string ActivityBasis { get; set; } = "";
}

public class AppUsage
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
    public double ForegroundSeconds { get; set; }
    public double MediaSeconds { get; set; }
    public double GamepadSeconds { get; set; }
    public double Share { get; set; }
}

public class DaySummary
{
    public string Date { get; set; } = "";
    public double ScreenSeconds { get; set; }
    public double ForegroundSeconds { get; set; }
    public double RawForegroundSeconds { get; set; }
    public bool HasLegacyActivity { get; set; }
    public double LockedSeconds { get; set; }
    public double ObservedSeconds { get; set; }
    public double UnknownSeconds { get; set; }
    public DateTimeOffset? FirstRecorded { get; set; }
    public DateTimeOffset? LastRecorded { get; set; }
    public List<AppUsage> Apps { get; set; } = new();
    public double[] ForegroundHourly { get; set; } = new double[24];
    public double[] RawForegroundHourly { get; set; } = new double[24];
    public double[] ScreenHourly { get; set; } = new double[24];
    public double[] ObservedHourly { get; set; } = new double[24];
    public double[] UnknownHourly { get; set; } = new double[24];
    public Dictionary<string, double[]> AppHourly { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double[]> AppForegroundHourly { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasData { get; set; }
    public bool HasGaps { get; set; }
    public bool HasHourlyDetails { get; set; } = true;
}
