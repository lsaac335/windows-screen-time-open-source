# ScreenTime 0.5.1 — User guide

[English overview](../README.en.md) · [中文使用说明](../使用说明.md)

ScreenTime is a portable Windows app that stores usage records locally. The current app interface is in Chinese. This guide gives English descriptions alongside the labels you will see in the app.

## Start recording

Extract the Windows x64 release and run **ScreenTime.exe**, keeping its DLLs and runtime configuration files in the same folder. Install Windows Desktop Runtime 8 or a compatible version if needed; the runtime is not bundled.

The app starts recording automatically by default and opens a desktop widget. It has no sample usage records and cannot retrieve activity from before it was running.

Double-click the widget or choose **打开完整面板** (Open full dashboard) to see history, app statistics, and settings. Choose **收起** (Collapse) to return to the widget. The dashboard is created when needed and released when closed; collection continues independently.

## Desktop widget and background operation

- Drag an empty part of the widget to move it. Its position is saved when you release it.
- Right-click the widget or select **…** for its menu. **锁定位置** locks its position; **浮动置顶** keeps it above other windows.
- **桌面浮窗** is an ordinary floating window that other windows can cover. **悬浮置顶** indicates that always-on-top mode is enabled.
- The widget has no taskbar button. Choose **显示小组件** (Show widget) in the system tray to bring it back, even if it is already enabled or has been covered.
- Widget pinning and dashboard pinning are separate. Use the pin icon in the dashboard title bar to control the dashboard.
- Choose **隐藏窗口，后台记录** (Hide windows and record in the background) in settings, the widget menu, or the tray menu to close both views while continuing collection. Restore the dashboard from the tray or by launching the app again.
- **隐藏组件** (Hide widget) returns to the full dashboard. Use the background-recording command when you want all app windows closed.

The widget is an independent app window; it does not require opening the Windows news/widgets panel. To stop the program completely, choose **退出** (Exit) in the tray or **退出程序** (Exit app) in settings. Closing the dashboard alone leaves background recording running.

## Settings

Open **设置** (Settings). These preferences are independent:

| Chinese label | English meaning and behavior |
|---|---|
| 开机自启 | Launch after signing in to Windows. Off by default for a new installation. |
| 开机自启时显示小组件 | Show the widget at sign-in. On by default; requires launch at sign-in to be enabled. Turn it off for a tray-only sign-in launch. |
| 打开软件自动开始记录 | Start recording automatically when launched. On by default; changes apply on the next launch. |
| 外观 | Appearance: **跟随系统** (System), **浅色** (Light), or **深色** (Dark). System is the default and follows the Windows app color mode. |
| 完整面板始终置顶 | Keep the dashboard on top; synchronized with its title-bar pin. Off by default. |
| 统计多个窗口的可见时间 | Track visible time across windows. On by default. Disabling releases its listeners while retaining recorded aggregates. |
| 从活跃时长中排除空闲 | Exclude inactivity beyond the configured threshold from active time. On by default, with a five-minute threshold. |
| 播放媒体时保留前台活跃计时 | Use matching media playback as additional activity evidence. Off by default. |
| 识别手柄输入 | Use supported XInput controller input as additional activity evidence. Off by default. |

Appearance changes apply to the dashboard, widget, and open app details without restarting. A manual light/dark choice overrides Windows until you select System again. The app reads the Windows preference and does not change Windows settings.

Idle thresholds can be 1, 3, 5, 10, 15, or 30 minutes. App details offer per-app overrides, including counting continuously while that app is in the foreground. Changes affect future records, not previously recorded totals. Pause/resume affects the current run; the automatic-recording preference determines what happens on the next launch.

Media detection works only with apps exposing system media controls and only when the playing source clearly matches the foreground app. It does not read media titles or artwork. Browsers can be matched at app level, not to a particular tab. Disabling stops detection; restarting with it disabled can also release previously loaded media components.

Controller detection uses XInput. A connected controller at rest is not activity: buttons, triggers, or stick movement beyond dead zones are required. Disconnected devices are checked every five seconds; connected devices are checked every 0.25 seconds. Other controller protocols may not be supported.

## Charts and app details

Use **使用概览** (Overview) or **应用统计** (App statistics). Select today, yesterday, a date, or the last seven days. Switch the chart and ranking metric with:

- **活跃时长** — Active time.
- **前台时长** — Foreground time.
- **可见时长** — Visible time.

The app chart shows up to ten apps on a shared scale starting at zero. Search filters both the chart and the list. Percentages use the selected metric across all apps in the selected date range; searching does not change the denominator. The full list is paginated with up to 25 entries per page.

Click a bar or app row for hourly usage, a seven-day trend, and the app's idle rule. ScreenTime itself is excluded from app statistics. Empty or missing periods are shown explicitly rather than filled with sample data.

## What the numbers mean

**Screen-on time** counts the local session's display-on or dimmed state reported by Windows, including screen-on time while locked. Multiple monitors do not multiply it. A monitor's physical power switch may not match Windows' reported state.

**Foreground time** belongs to the app holding foreground focus while the local session is unlocked and the display is on. It includes periods without input. Other visible windows, background music, and downloads do not add to this metric.

**Active time** removes the portion of inactivity beyond the idle threshold from foreground time. With a five-minute threshold, the first five idle minutes still count; later idle time is excluded until activity resumes. Foreground and screen-on recording continue. The app reads the last-input timestamp, not key contents. Reading or unsupported media/controller activity can still be misclassified as idle.

Optional matching media or controller evidence can restore otherwise excluded foreground activity. These intervals are already included in active time and must not be added again. Playback does not prove that someone is watching.

**Visible time** estimates how long enough of a window is displayed while the local session is unlocked and the display is on. It excludes minimized, fully covered, off-current-desktop, utility-overlay, and barely exposed windows. Inactivity does not stop it. Windows of the same app are merged; different apps may accumulate simultaneously. Their total may therefore exceed screen-on time. Do not add visible time to the other metrics.

Visibility is estimated from window rectangles, so transparent, irregular, and special windows can produce errors. Reconciliation takes roughly one to three seconds. Visible time indicates display presence, not attention, and cannot be reconstructed for earlier recordings.

Recordings from before version 0.3 did not retain app ownership during excluded idle periods. Their foreground values show **≥**, a known lower bound. New rules do not rewrite that history.

Gray hatching indicates missing or partial recording; pale areas indicate future time. App downtime, pauses, and unknown system state are not evidence of zero usage. Shutdown and sleep are not assigned to the previously foreground app. Tracking covers the current user's local active session; a local remote-desktop client is attributed to that client, without identifying apps inside the remote computer.

## Storage, export, and deletion

Records are stored in **data** beside the executable, including app identities/names, necessary icons, time intervals, and system state. No account is needed. The tracker does not read window titles, URLs, chat contents, screenshots, or keystroke contents, or upload usage records.

Records are normally saved about every ten seconds; a busy interface or disk can delay saving. An unexpected exit may lose unsaved activity. Missing periods are not backfilled. Detail and hourly data are retained for 90 days, and daily summaries for 365 days.

Visible time is stored separately in `data/visibility` as per-app hourly aggregates and daily totals, rather than a per-second window history. Its history cache holds up to eight dates. The main history cache also has count/size limits, with today's records retained.

Before migrating an old-format daily file, the app preserves a `.json.pre-v2` copy. Migration stops if that backup fails. Backups follow the 90-day detail retention policy and are included in date-range/all-record deletion. For a downgrade, exit the app and restore a complete pre-upgrade `data` backup.

CSV export covers the selected date or seven-day range and reports durations in seconds. CSV is intended for analysis, not complete restoration. Some fields need special care:

| CSV metric | Meaning |
|---|---|
| app_active / app_foreground | Active / foreground time for an app. |
| app_media_inferred / app_gamepad | Supplemental evidence already included in app_active. |
| app_visible | An app's estimated visible time. |
| visible_parallel_sum | Sum of app visible time, with overlap allowed. |
| visibility_coverage | Time for which usable window-visibility state was collected. |

`runtime-status.json` is a current diagnostic snapshot, not a full usage history. Deleting a selected range also removes its visible aggregates and managed migration backups; deleting all records also clears managed icon caches. Deletion does not stop future collection. Exported copies require separate handling.

## Updating or removing the app

Exit ScreenTime and back up `data` before replacing program files. Keep the existing `data` directory to retain history. Always keep the executable, DLLs, and runtime configuration together.

To remove the app, disable launch at sign-in in settings, then exit it. Preserve `data` if you want to keep your history; removing the portable directory also removes any records stored inside it.

## Current limits

This release has no app categories, daily limits, break reminders, website tracking, cloud sync, installer, or app signature. Full-day resource behavior and every physical lock/display-off/sleep/multi-monitor hardware combination have not been validated. Detailed validation reports are linked from the [English overview](../README.en.md).
