using System;
using System.IO;
using System.Text.Json;

namespace ScreenTime;

/// <summary>Settings migration and persistence checks; never opens the user's data directory or registry.</summary>
public static class SettingsSelfTests
{
    public static string Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "validation", "settings-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Settings self-test failed: " + name + " (test data: " + root + ")");
            checks++;
        }

        var fresh = AppSettings.Load(path);
        Check(fresh.AutoRecord && fresh.DarkMode, "new settings default to automatic recording and dark mode");
        Check(fresh.AutoStartWidget && fresh.IdleFilterEnabled, "new settings enable login widget display and idle filtering");
        Check(fresh.IdleTimeoutMinutes == 5 && fresh.IdleTimeoutOverrides.Count == 0,
            "new settings preserve five-minute idle policy without application overrides");
        Check(!fresh.AlwaysOnTop, "pinning is opt-in by default");
        Check(fresh.WidgetEnabled && !fresh.WidgetPinned && !fresh.WidgetLocked && fresh.WidgetLeft == null && fresh.WidgetTop == null,
            "new settings start in widget mode with no persisted placement");
        Check(!File.Exists(path), "loading absent settings does not create a usage or settings file");

        const string oldJson = "{\"RecordingEnabled\":true,\"Paused\":true,\"CreatedVersion\":\"0.1.0\"}";
        File.WriteAllText(path, oldJson);
        var migrated = AppSettings.Load(path);
        Check(migrated.AutoRecord && migrated.DarkMode, "old JSON missing new fields receives enabled defaults");
        Check(migrated.AutoStartWidget && migrated.IdleFilterEnabled, "legacy settings receive login-widget and idle-filter defaults");
        Check(migrated.IdleTimeoutMinutes == 5 && migrated.IdleTimeoutOverrides.Count == 0,
            "legacy settings keep the previous five-minute interpretation");
        Check(migrated.RecordingEnabled && migrated.Paused && migrated.CreatedVersion == "0.1.0", "loading legacy settings preserves existing fields for the startup policy");
        Check(migrated.WidgetEnabled && !migrated.WidgetPinned && !migrated.WidgetLocked && migrated.WidgetLeft == null && migrated.WidgetTop == null,
            "legacy settings use widget mode when no display preference was saved");
        Check(File.ReadAllText(path) == oldJson, "migration load alone does not rewrite legacy settings");

        File.WriteAllText(path, "{\"AutoRecord\":false,\"DarkMode\":false,\"RecordingEnabled\":true,\"Paused\":false}");
        var disabled = AppSettings.Load(path);
        Check(!disabled.AutoRecord && !disabled.DarkMode, "explicit disabled choices are not replaced by defaults");

        foreach (bool automatic in new[] { false, true })
        foreach (bool dark in new[] { false, true })
        {
            var settings = new AppSettings { AutoRecord = automatic, DarkMode = dark, RecordingEnabled = true, Paused = !automatic };
            settings.Save(path);
            var loaded = AppSettings.Load(path);
            Check(loaded.AutoRecord == automatic && loaded.DarkMode == dark, "both independent feature preferences round trip: " + automatic + "/" + dark);
            Check(loaded.RecordingEnabled && loaded.Paused == !automatic, "feature save preserves current-session state: " + automatic + "/" + dark);
            loaded.RecordingEnabled = !automatic;
            loaded.Paused = automatic;
            loaded.PrepareForLaunch();
            Check(loaded.RecordingEnabled == automatic && loaded.Paused == !automatic && loaded.DarkMode == dark,
                "launch applies automatic-record preference over prior session state, independently of theme: " + automatic + "/" + dark);
        }
        Check(!File.Exists(path + ".tmp"), "successful atomic save leaves no temporary settings copy");
        var pinned = new AppSettings { AlwaysOnTop = true, AutoRecord = false, DarkMode = false };
        pinned.Save(path);
        var pinnedLoaded = AppSettings.Load(path);
        Check(pinnedLoaded.AlwaysOnTop && !pinnedLoaded.AutoRecord && !pinnedLoaded.DarkMode, "pin preference persists independently of recording and appearance");

        var nextLaunchOnly = new AppSettings { AutoRecord = true, RecordingEnabled = true, Paused = false };
        nextLaunchOnly.AutoRecord = false;
        Check(nextLaunchOnly.RecordingEnabled && !nextLaunchOnly.Paused, "editing next-launch preference does not stop the current session");

        var widget = new AppSettings
        {
            WidgetEnabled = true, WidgetPinned = true, WidgetLocked = true,
            WidgetLeft = -120.5, WidgetTop = 180.25,
            AlwaysOnTop = false, AutoRecord = false, DarkMode = false, RecordingEnabled = true, Paused = false
        };
        widget.Save(path);
        var widgetLoaded = AppSettings.Load(path);
        Check(widgetLoaded.WidgetEnabled && widgetLoaded.WidgetPinned && widgetLoaded.WidgetLocked,
            "widget visibility, pinning and position lock persist independently");
        Check(widgetLoaded.WidgetLeft == -120.5 && widgetLoaded.WidgetTop == 180.25,
            "widget placement retains fractional and negative desktop coordinates");
        Check(!widgetLoaded.AlwaysOnTop && !widgetLoaded.AutoRecord && !widgetLoaded.DarkMode && widgetLoaded.RecordingEnabled && !widgetLoaded.Paused,
            "saving widget preferences preserves main-window, theme and recording choices");
        widgetLoaded.PrepareForLaunch();
        Check(widgetLoaded.WidgetEnabled && widgetLoaded.WidgetPinned && widgetLoaded.WidgetLocked &&
              widgetLoaded.WidgetLeft == -120.5 && widgetLoaded.WidgetTop == 180.25 &&
              !widgetLoaded.RecordingEnabled && widgetLoaded.Paused && !widgetLoaded.AlwaysOnTop && !widgetLoaded.DarkMode,
            "launch applies recording choice without resetting the widget or main-window preferences");
        widgetLoaded.WidgetEnabled = false;
        widgetLoaded.WidgetPinned = false;
        widgetLoaded.AlwaysOnTop = true;
        widgetLoaded.Save(path);
        var hiddenWidget = AppSettings.Load(path);
        Check(!hiddenWidget.WidgetEnabled && !hiddenWidget.WidgetPinned && hiddenWidget.WidgetLocked &&
              hiddenWidget.WidgetLeft == -120.5 && hiddenWidget.WidgetTop == 180.25 &&
              hiddenWidget.AlwaysOnTop && !hiddenWidget.AutoRecord && !hiddenWidget.DarkMode,
            "hiding a widget retains placement and lock without coupling its pin to the main window");

        var optional = new AppSettings { AutoStartWidget = false, IdleFilterEnabled = false };
        optional.Save(path);
        optional = AppSettings.Load(path);
        Check(!optional.AutoStartWidget && !optional.IdleFilterEnabled, "explicit disabled login-widget and idle-filter preferences persist");
        foreach (bool loginWidget in new[] { false, true })
        foreach (bool rememberedWidget in new[] { false, true })
        foreach (bool autoRecord in new[] { false, true })
        {
            var login = new AppSettings { AutoStartWidget = loginWidget, WidgetEnabled = rememberedWidget, AutoRecord = autoRecord, IdleFilterEnabled = false };
            login.PrepareForLaunch(autoStart: true);
            Check(login.WidgetEnabled == loginWidget && login.RecordingEnabled == autoRecord && !login.IdleFilterEnabled,
                "login display follows its own preference independently of remembered view and automatic recording");
            login.WidgetEnabled = rememberedWidget;
            login.PrepareForLaunch();
            Check(login.WidgetEnabled == rememberedWidget, "manual launch preserves the selected widget mode");
            login.PrepareForLaunch(autoStart: true, forceWidget: true);
            Check(login.WidgetEnabled, "explicit widget launch overrides the quiet login preference");
        }

        foreach (int minutes in new[] { 1, 3, 5, 10, 15, 30 })
        {
            var policy = new AppSettings { IdleTimeoutMinutes = minutes, IdleFilterEnabled = false };
            policy.IdleTimeoutOverrides["reader"] = 0;
            policy.IdleTimeoutOverrides["chat"] = 1;
            policy.Save(path);
            var reloaded = AppSettings.Load(path);
            Check(reloaded.IdleTimeoutMinutes == minutes && !reloaded.IdleFilterEnabled,
                "threshold and disabled filter round trip independently: " + minutes);
            Check(reloaded.IdleTimeoutOverrides["READER"] == 0 && reloaded.IdleTimeoutOverrides["chat"] == 1,
                "per-app overrides persist with case-insensitive identities");
            reloaded.IdleTimeoutOverrides.Remove("reader");
            reloaded.Save(path);
            Check(!AppSettings.Load(path).IdleTimeoutOverrides.ContainsKey("reader"), "removing override restores global policy");
        }

        File.WriteAllText(path, "{\"AutoRecord\":false,\"FutureOption\":\"unrecognized\"}");
        var future = AppSettings.Load(path);
        Check(!future.AutoRecord && future.DarkMode, "missing dark preference defaults independently of explicit auto-record preference");

        foreach (var invalid in new[] { "{invalid", "null", "{\"AutoRecord\":null}",
            "{\"IdleTimeoutMinutes\":0}", "{\"IdleTimeoutMinutes\":-1}", "{\"IdleTimeoutMinutes\":2}",
            "{\"IdleTimeoutOverrides\":null}", "{\"IdleTimeoutOverrides\":{\"app\":-1}}" })
        {
            File.WriteAllText(path, invalid);
            bool rejected = false;
            try { AppSettings.Load(path); }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(path) == invalid, "invalid settings raise an error and preserve original data: " + invalid);
        }

        return "Settings self-tests passed: " + checks + " assertions (legacy migration, independent feature preferences, widget placement, persistence, corrupt-file safety).";
    }
}
