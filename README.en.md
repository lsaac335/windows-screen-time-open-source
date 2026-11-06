# ScreenTime for Windows

[简体中文](README.md) | [English](README.en.md)

A local Windows desktop app for tracking screen-on time, foreground app time, estimated active time, and visible time across multiple windows. Current version: **0.5.1**.

[Download for Windows x64](https://github.com/lsaac335/windows-screen-time-open-source/releases/tag/v0.5.1) · [English user guide](docs/USER_GUIDE.en.md)

![ScreenTime dashboard illustration showing screen-on time, app usage, and daily activity](docs/images/overview.svg)

*Illustration with sample data. The app currently uses a Chinese interface; the English guide includes the corresponding Chinese control labels.*

## Features

![Illustration of app statistics and the desktop widget](docs/images/features.svg)

- Daily and seven-day views, hourly charts, app rankings, search, app details, and CSV export.
- Separate foreground, active, and visible app metrics. Visible time supports side-by-side windows and multiple monitors, with overlapping windows of the same app counted only once.
- A desktop widget shown by default, with no taskbar button. Open the full dashboard from the widget, then collapse it to return to the widget.
- Background recording with all app windows closed. Restore the dashboard from the system tray or by launching the app again.
- Appearance follows the Windows app theme by default, with manual light and dark options. Theme changes update open views without a restart.
- Optional idle filtering: five minutes by default, with 1/3/5/10/15/30-minute thresholds and per-app overrides.
- Foreground events collected on an independent thread, using event timestamps and one-second reconciliation.
- On-demand dashboard creation, bounded history and icon caches, and compact hourly aggregates for visible time. Visible-window tracking can be disabled independently.
- Optional system media and XInput controller detection, both off by default. Media components load only when enabled.
- Optional launch at Windows sign-in, automatic recording, and tray controls. ScreenTime itself is excluded from app usage statistics.
- Local storage with no account required. The tracker does not read window titles, website URLs, screenshots, or keystroke contents, and does not upload usage records.

## Understanding the metrics

| Metric | Meaning |
|---|---|
| Screen-on time | Time Windows reports the display as on or dimmed, including time on the lock screen. Multiple monitors do not multiply this total. |
| Foreground time | Time an app holds foreground focus while the local session is unlocked and the display is on, including idle periods. |
| Active time | Foreground time after removing inactivity beyond the configured idle threshold, with optional activity evidence. |
| Visible time | Estimated time enough of an app window is displayed on the current desktop. Keyboard/mouse inactivity does not stop this metric. |

Visible tracking filters out minimized, fully covered, off-current-desktop, utility-overlay, and barely exposed windows. **Different apps can accumulate visible time simultaneously, so their sum may exceed screen-on time. Multiple windows of the same app do not multiply its time.** Transparent, irregular, and special windows can introduce errors because occlusion is estimated using window rectangles. Historical visible time cannot be reconstructed for periods before this feature collected it.

Active and visible time are estimates, not proof of attention. Media playback supplements active time only when the system's playing source clearly matches the foreground app. Browser detection cannot identify the specific tab. Background playback and downloads do not add foreground time. Controller evidence applies only to the foreground app and filters stick dead zones.

Older recordings did not retain app ownership during excluded idle periods. Their foreground totals display **≥** as a known lower bound. Upgrades preserve existing statistics and back up old-format day files before migration. Visible aggregates use separate files and are included when deleting records.

## Download and run

1. Download `ScreenTime-v0.5.1-Windows-x64.zip` from the [release page](https://github.com/lsaac335/windows-screen-time-open-source/releases/tag/v0.5.1).
2. Extract the archive and run `ScreenTime.exe`. Keep the accompanying DLLs and runtime configuration files together.
3. Recording starts automatically by default. Open the full dashboard to view history or change settings.

Requires Windows x64 and Windows Desktop Runtime 8 or a compatible version. The portable package does not bundle the runtime.

Before upgrading, exit the old app and back up its `data` directory. Replace the program files while preserving that directory. The published package contains no personal usage records or account information.

## Build and validate

Build on Windows 10 version 1809 or later, x64, with the .NET SDK 6. The project uses WPF and Microsoft's Windows SDK WinRT interfaces. The first build may download dependencies from NuGet; the resulting app does not need a network connection for tracking.

```powershell
.\build.ps1
```

Build output is written to `首版` in the repository directory. Run the core logic checks with:

```powershell
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release
```

For measurement methods and known limits, see the validation reports for [multi-window tracking and background operation](docs/validation-0.5.0.md), [system theme following](docs/validation-0.5.1.md), and the [earlier resource comparison](docs/validation-0.4.0.md). These reports are in Chinese. Resource use varies with open views, runtime history, and recorded data; short measurements do not establish all-day performance.

## License

Released under the [MIT License](LICENSE).

## Contributing

Bug reports, feature ideas, documentation improvements, and code contributions are welcome. Fork the repository, create a branch in your fork, and open a pull request against this repository's `main` branch for maintainer review. See the bilingual [contribution guide](CONTRIBUTING.md).
