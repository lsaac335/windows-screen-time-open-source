using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenTime;

/// <summary>Supplement only keyboard-idle foreground intervals; time is never added twice.</summary>
public static class ActivityEvidence
{
    public static bool SourceMatches(string source, string? executable, string? aumid)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (!string.IsNullOrWhiteSpace(aumid) && source.Equals(aumid, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (Path.IsPathFullyQualified(source)) return source.Equals(executable, StringComparison.OrdinalIgnoreCase);
        return source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            source.Equals(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<UsageSegment> Supplement(UsageSegment segment, bool mediaPlaying,
        double? previousGamepadIdle, double? currentGamepadIdle, double timeoutSeconds)
    {
        if (segment.Kind != UsageRules.IdleAppKind) { yield return segment; yield break; }
        if (mediaPlaying)
        {
            yield return ActiveCopy(segment, "media");
            yield break;
        }
        if (!currentGamepadIdle.HasValue) { yield return segment; yield break; }
        var candidate = ActiveCopy(segment, "gamepad");
        foreach (var part in IdleTimeFilter.Split(candidate, true, previousGamepadIdle, currentGamepadIdle, timeoutSeconds))
        {
            if (part.Kind == UsageRules.IdleAppKind) part.ActivityBasis = "idle";
            yield return part;
        }
    }

    private static UsageSegment ActiveCopy(UsageSegment s, string basis) => new()
    {
        Start = s.Start, End = s.End, Seconds = s.Seconds, Kind = "app", AppId = s.AppId,
        AppName = s.AppName, ActivityTracked = true, ActivityBasis = basis
    };

    // XInput's documented dead zones reject resting stick drift. Triggers have a 30/255 threshold.
    public static bool MeaningfulGamepad(ushort buttons, byte leftTrigger, byte rightTrigger,
        short lx, short ly, short rx, short ry) => buttons != 0 || leftTrigger > 30 || rightTrigger > 30 ||
        (long)lx * lx + (long)ly * ly > 7849L * 7849 || (long)rx * rx + (long)ry * ry > 8689L * 8689;
}
