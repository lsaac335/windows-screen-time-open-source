using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ScreenTime;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private AppController? _controller;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private WidgetWindow? _widget;
    private Forms.ToolStripMenuItem? _pauseItem;
    private Forms.ToolStripMenuItem? _widgetItem;
    private bool _widgetMode;
    private bool _shuttingDown;
    private bool _quietWidgetStart;
    private bool _viewCleanupQueued;
    private bool _backgroundMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _quietWidgetStart = e.Args.Contains("--tray");
        if (e.Args.Contains("--theme-self-test"))
        {
            string output = Arg(e.Args, "--test-output")!;
            try { File.WriteAllText(output, SystemThemeSelfTests.Run(Path.Combine(Path.GetDirectoryName(output)!, "theme-test-" + Guid.NewGuid().ToString("N")))); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--resource-probe"))
        {
            string output = Arg(e.Args, "--test-output")!;
            try { File.WriteAllText(output, ResourceProbe.Run(Arg(e.Args, "--resource-probe") == "on")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--visibility-fixture"))
        { VisibilityNativeSelfTests.Fixture(Arg(e.Args, "--visibility-fixture")!); Shutdown(); return; }
        if (e.Args.Contains("--visibility-native-test"))
        {
            var output = Arg(e.Args, "--test-output")!;
            try { File.WriteAllText(output, VisibilityNativeSelfTests.Run(Path.Combine(Path.GetDirectoryName(output)!, "visibility-native-fixture"))); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--media-self-test"))
        {
            var output = Arg(e.Args, "--test-output") ?? Path.Combine(AppContext.BaseDirectory, "media-test.txt");
            try { File.WriteAllText(output, MediaSessionSelfTests.Run()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--sampler-self-test"))
        {
            var output = Arg(e.Args, "--test-output") ?? Path.Combine(AppContext.BaseDirectory, "sampler-test.txt");
            try { File.WriteAllText(output, TrackingServiceSelfTests.Run()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--self-test"))
        {
            var output = Arg(e.Args, "--test-output") ?? Path.Combine(AppContext.BaseDirectory, "self-test.txt");
            try { File.WriteAllText(output, CoreSelfTests.Run() + Environment.NewLine + SettingsSelfTests.Run() + Environment.NewLine + IdleFilterSelfTests.Run() + Environment.NewLine + NativeTimingSelfTests.Run() + Environment.NewLine + ActivityEvidenceSelfTests.Run() + Environment.NewLine + VisibilitySelfTests.Run()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Shutdown(1); }
            return;
        }
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _mutex = new Mutex(true, @"Local\ScreenTime.Local." + user, out var isNew);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ScreenTime.Show." + user);
        if (!isNew) { _activation.Set(); Shutdown(); return; }
        try
        {
            var directory = Arg(e.Args, "--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "data");
            _controller = new AppController(Path.GetFullPath(directory),
                autoStart: e.Args.Contains("--tray"), forceWidget: e.Args.Contains("--widget"));
            // Login launch shows the widget even if the previous session used the
            // main panel. Reuse the normal reveal path to keep it visible/recoverable.
            if (_controller.WidgetEnabled) _quietWidgetStart = false;
            _controller.Attach();
            _controller.ExitRequested += () => { _shuttingDown = true; _widget?.PrepareForExit(); _window?.PrepareForExit(); Shutdown(); };
            SetupTray();
            _controller.Updated += UpdateWidgetMode;
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowPanel())), null, Timeout.Infinite, false);
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                MessageBox.Show("操作未完成：\n" + args.Exception.Message + "\n\n已有记录不会被自动删除。", "屏幕时间", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            if (_controller.WidgetEnabled) UpdateWidgetMode();
            else if (!e.Args.Contains("--tray")) ShowPanel();
            _quietWidgetStart = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("屏幕时间未能启动：\n" + ex.Message + "\n\n请保留 data 目录中的文件，再检查存储位置和权限。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private void SetupTray()
    {
        if (_controller == null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开屏幕时间", null, (_, _) => ShowPanel());
        _widgetItem = new Forms.ToolStripMenuItem("显示小组件", null, (_, _) =>
        {
            try { RevealWidget(); }
            catch (Exception ex) { MessageBox.Show("组件设置未保存：" + ex.Message, "屏幕时间", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
        menu.Items.Add(_widgetItem);
        menu.Items.Add("隐藏窗口，后台记录", null, (_, _) => RunInBackground());
        _pauseItem = new Forms.ToolStripMenuItem("开始记录", null, (_, _) => _controller.TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开数据文件夹", null, (_, _) => _controller.OpenDataDirectory());
        menu.Items.Add("退出", null, (_, _) => _controller.Exit());
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "ScreenTime.exe") ?? System.Drawing.SystemIcons.Application;
        _tray = new Forms.NotifyIcon { Icon = icon, Text = "屏幕时间 · 尚未开始", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowPanel();
        _controller.Updated += UpdateTray;
        UpdateTray();
    }
    private void UpdateTray()
    {
        if (_controller == null || _tray == null || _pauseItem == null) return;
        var text = "屏幕时间 · " + _controller.StatusText;
        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        _pauseItem.Text = !_controller.IsRecordingEnabled ? "开始记录" : _controller.IsPaused ? "恢复记录" : "暂停记录";
        if (_widgetItem != null) _widgetItem.Checked = _controller.WidgetEnabled;
    }
    private void UpdateWidgetMode()
    {
        if (_shuttingDown || _backgroundMode || _controller == null) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateWidgetMode)); return; }
        bool enabled = _controller.WidgetEnabled;
        if (_widgetMode == enabled) return;
        _widgetMode = enabled;
        if (enabled)
        {
            EnsureWidget();
            _widget?.ShowWidget(reveal: !_quietWidgetStart);
            ReleasePanel();
        }
        else
        {
            _widget?.Hide();
            ShowPanel();
        }
    }
    private void RevealWidget()
    {
        if (_shuttingDown || _controller == null) return;
        _backgroundMode = false;
        _controller.WidgetEnabled = true;
        EnsureWidget();
        _widget?.ShowWidget(reveal: true);
        if (_widget?.IsVisible == true) ReleasePanel();
    }
    private void EnsureWidget()
    {
        if (_widget != null || _controller == null) return;
        var widget = new WidgetWindow(_controller);
        _widget = widget;
        widget.OpenDashboardRequested += () => ShowPanel(true);
        widget.BackgroundRequested += RunInBackground;
        widget.Closed += (_, _) =>
        {
            if (ReferenceEquals(_widget, widget)) _widget = null;
            if (ReferenceEquals(MainWindow, widget)) MainWindow = _window;
            // Sampling owns its own HWND, so recreating the component never stops recording.
            if (!_shuttingDown && !_backgroundMode && _controller.WidgetEnabled)
                Dispatcher.BeginInvoke(new Action(() => { if (_shuttingDown || _backgroundMode || !_controller.WidgetEnabled) return; EnsureWidget(); _widget?.ShowWidget(); }));
        };
    }
    private void ShowPanel(bool dashboard = false)
    {
        if (_shuttingDown || _controller == null) return;
        _backgroundMode = false;
        if (_window == null)
        {
            var panel = new MainWindow(_controller);
            _window = panel;
            MainWindow = panel;
            panel.WidgetRequested += RevealWidget;
            panel.BackgroundRequested += RunInBackground;
            panel.Closed += (_, _) =>
            {
                panel.WidgetRequested -= RevealWidget;
                panel.BackgroundRequested -= RunInBackground;
                if (ReferenceEquals(_window, panel)) _window = null;
                if (ReferenceEquals(MainWindow, panel)) MainWindow = _widget;
                _controller.TrimViewCaches();
                QueueViewCleanup();
            };
        }
        if (dashboard) _window.ShowDashboard();
        else _window.ShowAndActivate();
    }
    private void ReleasePanel()
    {
        _window?.Close();
    }
    private void RunInBackground()
    {
        if (_shuttingDown) return;
        _backgroundMode = true;
        _widget?.PrepareForExit();
        _widget?.Close();
        ReleasePanel();
        _controller?.TrimViewCaches();
        QueueViewCleanup();
    }
    private void QueueViewCleanup()
    {
        if (_viewCleanupQueued || _shuttingDown) return;
        _viewCleanupQueued = true;
        // Wait until close handlers release their local view references. This is a
        // user-triggered transition, not a periodic collection or working-set trim.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewCleanupQueued = false;
            if (!_shuttingDown && _window == null)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        if (_controller != null) _controller.Updated -= UpdateWidgetMode;
        _controller?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        if (_mutex != null) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _mutex.Dispose(); }
        base.OnExit(e);
    }
}
