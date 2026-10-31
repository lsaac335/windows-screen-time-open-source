using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ScreenTime;

try
{
    if (args.Length == 1 && args[0] == "--check-cache-budget") { Console.WriteLine(CacheBudgetChecks.Run()); return; }
    if (args.Length == 2 && args[0] == "--check-upgrade")
    {
        var before = new Dictionary<DateTime, (double Screen, double Active, double Locked)>();
        foreach (var file in Directory.EnumerateFiles(args[1], "????-??-??.json"))
        {
            var date = DateTime.ParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var summary = json.RootElement.GetProperty("Summary");
            before[date] = (summary.GetProperty("ScreenSeconds").GetDouble(), summary.GetProperty("ForegroundSeconds").GetDouble(), summary.GetProperty("LockedSeconds").GetDouble());
        }
        var store = new UsageStore(args[1]);
        foreach (var pair in before)
        {
            var day = store.GetDay(pair.Key);
            if (Math.Abs(day.ScreenSeconds - pair.Value.Screen) > .001 || Math.Abs(day.ForegroundSeconds - pair.Value.Active) > .001 || Math.Abs(day.LockedSeconds - pair.Value.Locked) > .001)
                throw new InvalidOperationException("Upgrade changed existing metrics for " + pair.Key.ToString("yyyy-MM-dd"));
        }
        store.Save();
        _ = new UsageStore(args[1]);
        Console.WriteLine("Upgrade check passed: " + before.Count + " dates retained their existing screen, active and locked totals, and reloaded successfully.");
        return;
    }
    Console.WriteLine(CoreSelfTests.Run());
    Console.WriteLine(SettingsSelfTests.Run());
    Console.WriteLine(IdleFilterSelfTests.Run());
    Console.WriteLine(NativeTimingSelfTests.Run());
    Console.WriteLine(ActivityEvidenceSelfTests.Run());
    Console.WriteLine(VisibilitySelfTests.Run());
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
