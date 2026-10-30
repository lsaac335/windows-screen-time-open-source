using System;
using System.Collections.Generic;

namespace ScreenTime;

public interface IAppController
{
    bool IsRecordingEnabled { get; }
    bool IsPaused { get; }
    bool HasError { get; }
    string StatusText { get; }
    string CurrentAppName { get; }
    string DisplayStateText { get; }
    string DataDirectory { get; }
    string TimeZoneId { get; }
    DateTime Today { get; }
    bool AutoStart { get; set; }
    bool AutoRecord { get; set; }
    bool AutoStartWidget { get; set; }
    bool IdleFilterEnabled { get; set; }
    int IdleTimeoutMinutes { get; set; }
    int? GetAppIdleTimeoutMinutes(string appId);
    void SetAppIdleTimeoutMinutes(string appId, int? minutes);
    bool DarkMode { get; set; }
    bool AlwaysOnTop { get; set; }
    bool WidgetEnabled { get; set; }
    bool WidgetPinned { get; set; }
    bool WidgetLocked { get; set; }
    double? WidgetLeft { get; }
    double? WidgetTop { get; }
    void SaveWidgetPosition(double left, double top);
    event Action? Updated;
    void StartRecording();
    void TogglePause();
    void Refresh();
    DaySummary GetDay(DateTime date);
    List<DaySummary> GetWeek(DateTime end);
    System.Windows.Media.ImageSource? GetAppIcon(string appId);
    void ExportCsv(string path, DateTime from, DateTime to);
    void DeleteRange(DateTime start, DateTime end);
    void DeleteAll();
    void OpenDataDirectory();
    void Exit();
}
