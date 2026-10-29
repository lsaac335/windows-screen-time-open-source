# 屏幕时间 ScreenTime

一款本地运行的 Windows 屏幕时间统计工具，展示亮屏时间、应用前台使用时间和应用排行。

![屏幕时间 ScreenTime 界面示意：亮屏时间、前台使用时间与每日分布](docs/images/overview.svg)

*界面示意图，数据为示例。*

## 功能介绍

![应用统计和桌面小组件的功能示意](docs/images/features.svg)

- 横向小窗口、完整统计面板和桌面浮动小组件。
- 按日期或近七天查看应用时长、柱状图和小时分布。
- 可选空闲过滤：连续 5 分钟无键鼠操作后暂停应用计时，亮屏时间独立累计。
- 深色模式、托盘运行、可选开机自启和 CSV 导出。
- 数据保存在本机，不读取窗口标题、网址或按键内容。

阅读、视频或手柄操作可能触发空闲过滤，可在设置关闭。应用时间以获得焦点的前台窗口为准，不统计后台应用；本程序自身排除在应用统计之外。

## 下载使用

访问 [Windows 版本下载页](https://github.com/lsaac335/windows-screen-time-open-source/releases)，下载 Windows x64 压缩包，解压后运行 `ScreenTime.exe`。需要 Windows Desktop Runtime 8 或兼容版本。

## 构建

需要 Windows x64 与 .NET SDK 6。项目使用 WPF，运行需 Windows Desktop Runtime 8 或兼容版本。在项目目录运行：

```powershell
.\build.ps1
```

编译结果位于 `首版` 文件夹，启动 `ScreenTime.exe`。更多操作说明见 [使用说明](使用说明.md)。

本项目目前为试用版本，使用数据保存在程序目录的 `data` 文件夹中。

## 开源许可

本项目采用 [MIT License](LICENSE)。
