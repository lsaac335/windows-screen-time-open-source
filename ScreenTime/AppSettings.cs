using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenTime;

public sealed class AppSettings
{
    public bool RecordingEnabled { get; set; }
    public bool Paused { get; set; }
    public bool AutoRecord { get; set; } = true;
    public bool AutoStartWidget { get; set; } = true;
    public bool IdleFilterEnabled { get; set; } = true;
    public bool MediaDetectionEnabled { get; set; }
    public bool GamepadDetectionEnabled { get; set; }
    public bool VisibilityDetectionEnabled { get; set; } = true;
    public int IdleTimeoutMinutes { get; set; } = 5;
    public Dictionary<string, int> IdleTimeoutOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DarkMode { get; set; } = true;
    public bool FollowSystemTheme { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool WidgetEnabled { get; set; } = true;
    public bool WidgetPinned { get; set; }
    public bool WidgetLocked { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public string CreatedVersion { get; set; } = "0.5.1";

    public static bool IsValidIdleTimeout(int minutes, bool allowNever = false) =>
        minutes is 1 or 3 or 5 or 10 or 15 or 30 || (allowNever && minutes == 0);

    private void ValidateIdleSettings()
    {
        if (!IsValidIdleTimeout(IdleTimeoutMinutes) || IdleTimeoutOverrides == null)
            throw new InvalidDataException("空闲计时设置无效，请检查设置文件。原文件已保留。");
        foreach (var rule in IdleTimeoutOverrides)
            if (string.IsNullOrWhiteSpace(rule.Key) || !IsValidIdleTimeout(rule.Value, true))
                throw new InvalidDataException("应用空闲计时设置无效。原文件已保留。");
    }

    public void PrepareForLaunch(bool autoStart = false, bool forceWidget = false)
    {
        RecordingEnabled = AutoRecord;
        Paused = !AutoRecord;
        if (autoStart) WidgetEnabled = AutoStartWidget;
        if (forceWidget) WidgetEnabled = true;
    }

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("设置文件为空，请保留文件后检查数据目录。");
        settings.ValidateIdleSettings();
        settings.IdleTimeoutOverrides = new Dictionary<string, int>(settings.IdleTimeoutOverrides, StringComparer.OrdinalIgnoreCase);
        return settings;
    }

    public void Save(string path)
    {
        ValidateIdleSettings();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
