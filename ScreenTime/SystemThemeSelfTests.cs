using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ScreenTime;

public static class SystemThemeSelfTests
{
    public static string Run(string root)
    {
        // Inject only the registry value; exercise real HWND notifications, UI dispatch,
        // controller persistence, window palette and disposal without changing Windows.
        bool systemDark = false;
        int checks = 0, updates = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Theme test: " + name); checks++; }
        Directory.CreateDirectory(root);
        using var controller = new AppController(root, systemThemeReader: () => systemDark);
        controller.Updated += () => updates++;
        Check(controller.FollowSystemTheme && !controller.DarkMode, "starts in the injected system light mode");
        var window = new Window { Width = 360, Height = 160, ShowInTaskbar = false, ShowActivated = false, Title = "ScreenTime theme verification" };
        var label = new TextBlock { Text = "Theme verification", Margin = new Thickness(20) };
        window.Content = label;
        ThemeManager.Apply(window, controller.DarkMode);
        label.Foreground = ThemeManager.GetBrush("Muted");
        window.Show();
        void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.ApplicationIdle);
            Dispatcher.PushFrame(frame);
        }
        void Notify() => SendMessage(controller.SystemTheme.Handle, 0x001A, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Pump(); systemDark = true;
            Notify(); Notify(); Notify();
            Check(updates == 0, "notifications are deferred and coalesced");
            Pump();
            Check(updates == 1 && controller.DarkMode, "one native notification burst updates appearance once");
            Check(ThemeManager.IsDark && ReferenceEquals(window.Resources["WindowBackground"], ThemeManager.GetBrush("WindowBackground")), "existing window follows the changed palette");
            Check(ReferenceEquals(label.Foreground, ThemeManager.GetBrush("Muted")), "code-assigned detail text follows palette changes");
            Notify(); Pump(); Check(updates == 1, "unchanged Windows value does not repaint");
            controller.DarkMode = false;
            Check(!controller.FollowSystemTheme && !controller.DarkMode, "manual light overrides system dark");
            int manualUpdates = updates;
            systemDark = false; Notify(); Pump(); systemDark = true; Notify(); Pump();
            Check(updates == manualUpdates && !controller.DarkMode, "system changes preserve manual light");
            controller.DarkMode = true;
            systemDark = false; Notify(); Pump();
            Check(!controller.FollowSystemTheme && controller.DarkMode, "manual dark survives a system light change");
            controller.FollowSystemTheme = true;
            Check(!controller.DarkMode && !ThemeManager.IsDark, "returning to system applies its latest cached value immediately");
            Check(ReferenceEquals(label.Foreground, ThemeManager.GetBrush("Muted")), "detail palette also returns to light");
            var saved = AppSettings.Load(Path.Combine(root, "settings.json"));
            Check(saved.FollowSystemTheme && saved.DarkMode, "system preference and manual fallback persist separately");
            Notify(); controller.Dispose(); Pump();
            Check(!window.Dispatcher.HasShutdownStarted, "disposing with a queued callback leaves the UI dispatcher alive");
        }
        finally { window.Close(); }
        return "Theme integration tests passed: " + checks + " assertions (native notifications, debouncing, manual overrides, existing windows/detail palettes, persistence, disposal; Windows settings unchanged).";
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
