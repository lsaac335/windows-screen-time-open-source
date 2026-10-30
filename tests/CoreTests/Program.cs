using System;
using ScreenTime;

try
{
    Console.WriteLine(CoreSelfTests.Run());
    Console.WriteLine(SettingsSelfTests.Run());
    Console.WriteLine(IdleFilterSelfTests.Run());
    Console.WriteLine(NativeTimingSelfTests.Run());
    Console.WriteLine(ActivityEvidenceSelfTests.Run());
    Console.WriteLine(CacheBudgetChecks.Run());
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
