using System;
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
    public bool DarkMode { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool WidgetEnabled { get; set; } = true;
    public bool WidgetPinned { get; set; }
    public bool WidgetLocked { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public string CreatedVersion { get; set; } = "0.2.1";

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
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("设置文件为空，请保留文件后检查数据目录。");
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
