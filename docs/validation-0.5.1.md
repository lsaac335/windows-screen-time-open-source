# ScreenTime 0.5.1：外观跟随系统

日期：2026-10-31（Asia/Hong_Kong）；以下为0.5.1发布前的本机验收记录。

- 设置“外观”提供跟随系统、浅色、深色；组件菜单提供同样三个选项。新版本默认跟随系统，旧设置缺少 FollowSystemTheme 时补为 true。显式手动模式保存为 false；DarkMode 原字段保留手动选择，实际颜色由两项共同解析。
- 跟随 Windows 的默认应用颜色偏好 AppsUseLightTheme，区别于任务栏使用的 SystemUsesLightTheme。仅只读，不修改操作系统设置。[微软文档](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/apps/develop/settings/settings-common.md)
- 在现有 UI 线程上保留一个不可见 HWND 接收 WM_SETTINGCHANGE/WM_THEMECHANGED 和恢复通知；合并同批通知，只有颜色变化才更新。没有轮询、额外线程或 WinRT 媒体组件。即使所有视图关闭、尚未开始记录，也仍能监听；关闭程序时释放 HWND、移除事件。
- 更新主面板、小组件、图表和已经打开的应用详情；更新代码赋值的主题画笔，不重建弹窗。手动指定浅色或深色后，系统变化不会覆盖选择；恢复跟随后立即采用已缓存的系统当前颜色。
- Release 0 警告/0 错误；301 项原有逻辑及设置检查通过；13 项主题集成检查通过，包括真实 HWND 通知、合并去重、手动覆盖、已打开窗口/详情画笔、设置持久化和带队列释放。
- 主题集成检查注入颜色值，向自身 HWND 发送通知；没有切换 Windows 的全局颜色设置。实际电脑只读检测为应用深色/系统深色，已验证本地跟随深色。实际 UI 从跟随系统→手动浅色→跟随系统，主面板和组件即时换色，最终保持跟随系统。
- 本地程序已更新 D:\屏幕使用时间\首版\ScreenTime.exe 为0.5.1并重启，正在记录；8 天原计时总量保持或正常增加，非外观设置保留。旧程序文件、源码和数据副本保留在 qa/v0.5.1-before-*。
- 便携包 ScreenTime-v0.5.1-Windows-x64.zip 已更新，9个发布文件，不含data、PDB或测试输出；SHA256 C8F953ECA8A7CB1FFA1EFAC2D7EB75CB04022DE48F841D4977E501C6C422259D。
- 本版源码与便携包通过0.5.1发布页提供。该次功能没有复测全天内存或改变0.5.0计时口径。
