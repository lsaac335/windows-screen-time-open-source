using System;
using Microsoft.Win32;

namespace ScreenTime;

internal static class AutoStartManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "ScreenTime.Local";
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            return string.Equals(key?.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    private static string Command => "\"" + Environment.ProcessPath + "\" --tray";
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled) key.SetValue(Entry, Command, RegistryValueKind.String);
        else if (string.Equals(key.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(Entry, false);
    }
}
