using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ScreenTime;

public static class ResourceProbe
{
    public static string Run(bool visibility)
    {
        using var sampler = new TrackingService(true, 5, new Dictionary<string, int>(), false,
            mediaDetection: false, gamepadDetection: false, visibilityDetection: visibility);
        using var process = Process.GetCurrentProcess();
        int samples = 13, intervals = 0;
        var rows = new List<object>();
        Thread.Sleep(10000);
        sampler.DrainSegments(_ => { }); sampler.DrainVisible(_ => intervals++);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh(); double cpuStart = process.TotalProcessorTime.TotalSeconds;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++)
        {
            sampler.DrainSegments(_ => { }); sampler.DrainVisible(_ => intervals++);
            process.Refresh();
            rows.Add(new { Seconds = clock.Elapsed.TotalSeconds, WorkingSetMiB = process.WorkingSet64 / 1048576d,
                PrivateMiB = process.PrivateMemorySize64 / 1048576d, Cpu = process.TotalProcessorTime.TotalSeconds - cpuStart, Handles = process.HandleCount });
            if (i + 1 < samples) Thread.Sleep(5000);
        }
        return JsonSerializer.Serialize(new { Visibility = visibility, Mode = "sampler only; no dashboard/widget/tray or usage persistence", Samples = rows,
            Seconds = clock.Elapsed.TotalSeconds, CpuSeconds = process.TotalProcessorTime.TotalSeconds - cpuStart, VisibilityIntervals = intervals,
            LoadedMediaProjection = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Microsoft.Windows.SDK.NET"), Native = sampler.DiagnosticText }, new JsonSerializerOptions { WriteIndented = true });
    }
}
