# 开发与验证

用户可运行版本在项目根目录的 `首版/`，源代码在本目录。当前版本 0.3.0：独立采集线程、前台事件原始时间戳、前台/活跃双指标、全局与按应用空闲规则。默认显示桌面小组件，点击组件打开完整面板，点击“收起”返回组件。

## 构建

在项目根目录的 PowerShell 中运行 `.\build.ps1`。脚本只编译本项目，不安装服务，不开启登录自启动，不上传文件，不清除已有 `首版/data`。

项目不依赖第三方 NuGet 包。当前机器使用已安装 .NET SDK 6 编译，运行时配置 `LatestMajor`，实际在已安装 .NET Windows Desktop Runtime 8.0.21 上验证。项目明确为 Windows x64，WPF 是主界面，WinForms 仅用于托盘。

## 主要模块

- `NativeTracker.cs`：本地前台进程、显示状态、WTS 会话、电源事件、单调计时。
- `TrackingService.cs`：独立 STA 消息窗口、前台事件监听和每秒采样，通过队列把片段交给 UI 线程持久化。
- `ForegroundEventClock.cs`：将 WinEvent 毫秒时间映射到单调时间，处理回绕、过旧、乱序和已提交边界。
- `IdleTimeFilter.cs`：按全局或应用阈值拆分活跃/空闲片段，保留前台归属、亮屏与观测覆盖。
- `UsageStore.cs` / `Models.cs`：跨日期与小时切分、本地原子保存、汇总、导出、保留与删除。
- `AppController.cs`：采集、界面与设置之间的协调，每秒采集、每十秒保存。
- `MainWindow.xaml` / `.cs` / `UsageChart.cs`：完整面板、原生图表及交互。
- `ThemeManager.cs` / `Theme.xaml`：深浅语义色、窗口标题栏、图表和控件主题。
- `AutoStartManager.cs`：用户主动开启时，设置当前用户的 `ScreenTime.Local` 登录启动项；默认关闭。

## 验证入口

纯数据测试：`ScreenTime.exe --self-test --test-output <结果文件绝对路径>`。测试数据写入该测试程序旁独立的 `self-test-data` 子目录，不读写用户的正式数据。独立控制台测试项目位于根目录 `validation/CoreTests/`。

真实桌面验证可使用 `ScreenTime.exe --data-dir <独立测试目录>`。主程序按当前 Windows 用户保持单实例，重复打开会显示已经运行的窗口。不要同时启动正式记录和 QA 记录两个采集实例。

系统 API 的测试需要正常交互用户上下文，受限沙箱可能无法查询 WTS 信息。这不是要求管理员运行；应用清单为 `asInvoker`。

登录启动沿用 `--tray` 参数：按独立的 `AutoStartWidget` 偏好显示小组件（默认开启）或仅托盘运行，不取决于上次关闭时是否显示组件。新安装手动启动默认显示小组件；已有设置仍保留上次组件选择，`--widget` 显式指定组件模式；这些显示选择不改变自动记录开关。

2026-10-30 数据/设置/空闲/事件时钟共 208 项自检；`--sampler-self-test --test-output <绝对路径>` 另验证独立线程、阻塞 UI、队列重试和退出末段，共 11 项。旧格式升级先保存 `.json.pre-v2`，旧数据原始前台为下限。数据层内部兼容字段 `ForegroundSeconds` 表示活跃，`RawForegroundSeconds` 表示前台；CSV 用明确指标名导出。

`qa/native-probe/native-probe.txt` 保留了原生事件与初始状态验证结果。合成状态测试不等于物理锁屏/睡眠/关屏已经验证。
