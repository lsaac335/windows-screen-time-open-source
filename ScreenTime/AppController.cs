using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace ScreenTime;

public sealed class AppController : IAppController, IDisposable
{
    private readonly UsageStore _store;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly DispatcherTimer _timer;
    private NativeTracker? _tracker;
    private IntPtr _handle;
    private string? _error;
    private int _ticks;
    private bool _disposed;
    private bool _exiting;
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> _icons = new();
    public event Action? Updated;
    public event Action? ExitRequested;
    public bool IsRecordingEnabled => _settings.RecordingEnabled;
    public bool IsPaused => !_settings.RecordingEnabled || _settings.Paused;
    public bool HasError => _error != null || (_tracker?.StatusText.Contains("异常", StringComparison.Ordinal) ?? false);
    public string StatusText => _error != null ? "采集异常 · " + _error : !_settings.RecordingEnabled ? "尚未开始记录" : _tracker?.StatusText ?? "正在初始化";
    public string CurrentAppName => _tracker?.CurrentAppName ?? "等待开始记录";
    public string DisplayStateText => _tracker?.DisplayStateText ?? "尚未采集";
    public string DataDirectory => _store.DataDirectory;
    public string TimeZoneId => _store.TimeZoneId;
    public DateTime Today => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, TimeZoneId).Date;
    public bool AutoStart { get => AutoStartManager.IsEnabled; set { AutoStartManager.Set(value); Updated?.Invoke(); } }
    public bool AutoStartWidget
    {
        get => _settings.AutoStartWidget;
        set
        {
            if (_settings.AutoStartWidget == value) return;
            bool previous = _settings.AutoStartWidget;
            SaveWidgetPreference(() => _settings.AutoStartWidget = value, () => _settings.AutoStartWidget = previous);
        }
    }
    public bool IdleFilterEnabled
    {
        get => _settings.IdleFilterEnabled;
        set
        {
            if (_settings.IdleFilterEnabled == value) return;
            bool previous = _settings.IdleFilterEnabled;
            _tracker?.SetIdleFilter(value);
            _settings.IdleFilterEnabled = value;
            try { _settings.Save(_settingsPath); }
            catch { _settings.IdleFilterEnabled = previous; _tracker?.SetIdleFilter(previous); throw; }
            Updated?.Invoke();
        }
    }
    public bool AutoRecord
    {
        get => _settings.AutoRecord;
        set
        {
            if (_settings.AutoRecord == value) return;
            var previous = _settings.AutoRecord;
            _settings.AutoRecord = value;
            try { _settings.Save(_settingsPath); }
            catch { _settings.AutoRecord = previous; throw; }
            Updated?.Invoke();
        }
    }
    public bool DarkMode
    {
        get => _settings.DarkMode;
        set
        {
            if (_settings.DarkMode == value) return;
            var previous = _settings.DarkMode;
            _settings.DarkMode = value;
            try { _settings.Save(_settingsPath); }
            catch { _settings.DarkMode = previous; throw; }
            Updated?.Invoke();
        }
    }
    public bool AlwaysOnTop
    {
        get => _settings.AlwaysOnTop;
        set
        {
            if (_settings.AlwaysOnTop == value) return;
            var previous = _settings.AlwaysOnTop;
            _settings.AlwaysOnTop = value;
            try { _settings.Save(_settingsPath); }
            catch { _settings.AlwaysOnTop = previous; throw; }
            Updated?.Invoke();
        }
    }

    public bool WidgetEnabled
    {
        get => _settings.WidgetEnabled;
        set
        {
            if (_settings.WidgetEnabled == value) return;
            bool previous = _settings.WidgetEnabled;
            SaveWidgetPreference(() => _settings.WidgetEnabled = value, () => _settings.WidgetEnabled = previous);
        }
    }
    public bool WidgetPinned
    {
        get => _settings.WidgetPinned;
        set
        {
            if (_settings.WidgetPinned == value) return;
            bool previous = _settings.WidgetPinned;
            SaveWidgetPreference(() => _settings.WidgetPinned = value, () => _settings.WidgetPinned = previous);
        }
    }
    public bool WidgetLocked
    {
        get => _settings.WidgetLocked;
        set
        {
            if (_settings.WidgetLocked == value) return;
            bool previous = _settings.WidgetLocked;
            SaveWidgetPreference(() => _settings.WidgetLocked = value, () => _settings.WidgetLocked = previous);
        }
    }
    public double? WidgetLeft => _settings.WidgetLeft;
    public double? WidgetTop => _settings.WidgetTop;
    public void SaveWidgetPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) throw new ArgumentException("组件位置无效。");
        if (_settings.WidgetLeft.HasValue && _settings.WidgetTop.HasValue &&
            Math.Abs(_settings.WidgetLeft.Value - left) < .5 && Math.Abs(_settings.WidgetTop.Value - top) < .5) return;
        double? oldLeft = _settings.WidgetLeft, oldTop = _settings.WidgetTop;
        SaveWidgetPreference(() => { _settings.WidgetLeft = left; _settings.WidgetTop = top; },
            () => { _settings.WidgetLeft = oldLeft; _settings.WidgetTop = oldTop; });
    }
    private void SaveWidgetPreference(Action update, Action rollback)
    {
        update();
        try { _settings.Save(_settingsPath); }
        catch { rollback(); throw; }
        Updated?.Invoke();
    }

    public AppController(string dataDirectory, bool autoStart = false, bool forceWidget = false)
    {
        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _settings = AppSettings.Load(_settingsPath);
        _settings.PrepareForLaunch(autoStart, forceWidget);
        _store = new UsageStore(dataDirectory);
        _store.Prune();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public void Attach(IntPtr windowHandle)
    {
        _handle = windowHandle;
        if (_settings.RecordingEnabled) EnsureTracker();
        _settings.Save(_settingsPath);
        _timer.Start();
    }

    private void EnsureTracker()
    {
        if (_tracker != null || _handle == IntPtr.Zero) return;
        _tracker = new NativeTracker(_handle, _settings.IdleFilterEnabled);
        _tracker.Faulted += ex => { _error = ex.Message; Updated?.Invoke(); };
        _tracker.SegmentReady += segment => _store.Add(segment);
        _tracker.StateChanged += () => Updated?.Invoke();
        _tracker.Start(_settings.Paused);
    }

    public void StartRecording()
    {
        _settings.RecordingEnabled = true;
        _settings.Paused = false;
        try
        {
            EnsureTracker();
            _tracker?.SetPaused(false);
            _settings.Save(_settingsPath);
            _error = null;
        }
        catch (Exception ex) { _error = ex.Message; }
        Updated?.Invoke();
    }

    public void TogglePause()
    {
        if (!_settings.RecordingEnabled) { StartRecording(); return; }
        var previous = _settings.Paused;
        try
        {
            _tracker?.SetPaused(!previous);
            _settings.Paused = !previous;
            _settings.Save(_settingsPath);
            _store.Save();
            _error = null;
        }
        catch (Exception ex) { _error = ex.Message; }
        Updated?.Invoke();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            _tracker?.Tick();
            _ticks++;
            if (_ticks % 10 == 0)
            {
                _tracker?.Flush();
                _store.Save();
                if (_ticks % 3600 == 0) _store.Prune();
                WriteRuntimeStatus();
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        if (_ticks % 5 == 0 || _error != null) Updated?.Invoke();
    }

    private void WriteRuntimeStatus()
    {
        var path = Path.Combine(DataDirectory, "runtime-status.json");
        var widget = System.Windows.Application.Current?.Windows.OfType<WidgetWindow>().FirstOrDefault();
        var snapshot = new
        {
            Version = "0.2.0", ProcessId = Environment.ProcessId,
            Runtime = Environment.Version.ToString(), UpdatedAt = DateTimeOffset.Now,
            RecordingEnabled = IsRecordingEnabled, Paused = IsPaused,
            AutoRecord = AutoRecord, AutoStartWidget, IdleFilterEnabled,
            InputIdleSeconds = _tracker?.InputIdleSeconds, IsIdle = _tracker?.IsIdle,
            DarkMode = DarkMode, AlwaysOnTop = AlwaysOnTop,
            WidgetEnabled, WidgetPinned, WidgetLocked, WidgetLeft, WidgetTop,
            WidgetVisible = widget?.IsVisible,
            WidgetIsActive = widget?.IsActive,
            WidgetTopmost = widget?.Topmost,
            WidgetShowInTaskbar = widget?.ShowInTaskbar,
            WidgetDesktopAttached = widget != null && DesktopWidgetHost.IsAttached(widget),
            WidgetMode = widget?.ModeName,
            WidgetModeDetail = widget?.ModeDetail,
            WidgetScreenPosition = widget == null ? null : new { DesktopWidgetHost.GetScreenPosition(widget).Left, DesktopWidgetHost.GetScreenPosition(widget).Top },
            WindowTopmost = System.Windows.Application.Current?.MainWindow?.Topmost,
            WindowWidth = System.Windows.Application.Current?.MainWindow?.ActualWidth,
            WindowHeight = System.Windows.Application.Current?.MainWindow?.ActualHeight,
            Status = StatusText, Display = DisplayStateText,
            CurrentApp = CurrentAppName, Native = _tracker?.DiagnosticText,
            TimeZone = TimeZoneId
        };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    public void Refresh()
    {
        try { _tracker?.Flush(); _store.Save(); _error = null; }
        catch (Exception ex) { _error = ex.Message; }
        Updated?.Invoke();
    }
    public DaySummary GetDay(DateTime date) => _store.GetDay(date);
    public List<DaySummary> GetWeek(DateTime end) => _store.GetWeek(end);
    public System.Windows.Media.ImageSource? GetAppIcon(string appId)
    {
        if (_icons.TryGetValue(appId, out var cached)) return cached;
        var directory = Path.Combine(DataDirectory, "icons");
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appId))).Substring(0, 24);
        var file = Path.Combine(directory, key + ".png");
        System.Windows.Media.Imaging.BitmapSource? source = null;
        try
        {
            if (File.Exists(file))
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(file);
                image.EndInit();
                image.Freeze();
                source = image;
            }
            else
            {
                var path = _tracker?.GetApplicationPath(appId);
                if (path != null && File.Exists(path))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32));
                        source.Freeze();
                        try
                        {
                            Directory.CreateDirectory(directory);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                            using var stream = File.Create(file);
                            encoder.Save(stream);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException) { }
        // A null before the first native path resolution is retried on the next refresh.
        if (source != null) _icons[appId] = source;
        return source;
    }
    private void ClearIconCache()
    {
        _icons.Clear();
        var directory = Path.Combine(DataDirectory, "icons");
        if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.png")) File.Delete(file);
    }
    public void ExportCsv(string path, DateTime from, DateTime to)
    {
        FlushRequired();
        _store.ExportCsv(path, from, to);
    }
    private void FlushRequired() { _tracker?.Flush(); _store.Save(); }
    public void DeleteRange(DateTime start, DateTime end)
    {
        FlushRequired();
        _store.DeleteRange(start, end);
        ClearIconCache();
        WriteRuntimeStatus();
        Updated?.Invoke();
    }
    public void DeleteAll()
    {
        FlushRequired();
        _store.DeleteAll();
        ClearIconCache();
        WriteRuntimeStatus();
        Updated?.Invoke();
    }
    public void OpenDataDirectory() => Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
    public void Exit()
    {
        if (_exiting) return;
        try { FlushRequired(); _settings.Save(_settingsPath); }
        catch (Exception ex)
        {
            var result = System.Windows.MessageBox.Show("最后一段记录保存失败：\n" + ex.Message + "\n\n仍然退出？", "屏幕时间", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }
        _exiting = true;
        Dispose();
        ExitRequested?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Exception? failure = null;
        try { _tracker?.Dispose(); }
        catch (Exception ex) { failure = ex; }
        try { _store.Save(); _settings.Save(_settingsPath); }
        catch (Exception ex) { failure ??= ex; }
        if (failure != null)
        {
            _error = failure.Message;
            System.Windows.MessageBox.Show("退出时未能保存最后一段记录：\n" + failure.Message + "\n\n此前保存的数据仍在 data 文件夹中。", "保存未完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
