using System;
using System.Collections.Generic;

namespace ScreenTime;

/// <summary>Filters app attribution only; excluded time remains observed screen-on time.</summary>
public static class IdleTimeFilter
{
    public const double TimeoutSeconds = 300;

    public static IEnumerable<UsageSegment> Split(UsageSegment segment, bool enabled,
        double? previousIdleSeconds, double? currentIdleSeconds)
    {
        double duration = (segment.End - segment.Start).TotalSeconds;
        if (!enabled || segment.Kind != "app" || duration <= 0 || !Valid(currentIdleSeconds))
        {
            yield return segment;
            yield break;
        }

        double age = currentIdleSeconds!.Value;
        // A new input within this interval can end an idle period. Preserve both
        // sides of that boundary instead of assigning the entire tick to the app.
        double inputAt = age < duration ? duration - age : duration;
        double activeUntil = age < duration
            ? Valid(previousIdleSeconds) ? Math.Clamp(TimeoutSeconds - previousIdleSeconds!.Value, 0, inputAt) : inputAt
            : Math.Clamp(TimeoutSeconds - (age - duration), 0, duration);

        if (activeUntil > 0) yield return Part(segment, 0, activeUntil, false, duration);
        if (inputAt > activeUntil) yield return Part(segment, activeUntil, inputAt, true, duration);
        if (duration > inputAt) yield return Part(segment, inputAt, duration, false, duration);
    }

    private static bool Valid(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;

    private static UsageSegment Part(UsageSegment source, double from, double to, bool idle, double duration) => new()
    {
        Start = source.Start.AddSeconds(from),
        End = to == duration ? source.End : source.Start.AddSeconds(to),
        Seconds = source.Seconds * (to - from) / duration,
        Kind = idle ? UsageRules.ScreenOnlyKind : source.Kind,
        AppId = idle ? "" : source.AppId,
        AppName = idle ? "" : source.AppName
    };
}
