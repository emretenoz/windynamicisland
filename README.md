# WinDynamicIsland

A lightweight Windows Dynamic Island-style WPF widget for media, notifications, screenshots, timers, audio controls, and privacy indicators.

## Features

- Media detection through Windows GSMTC, with album art and playback controls on hover.
- Notification previews with app logos.
- Screenshot previews directly inside the island.
- Timer presets, custom timer dialog, compact countdown, and hover progress view.
- Mouse wheel volume control and audio output switching.
- Camera and microphone privacy dots.
- Fullscreen auto-hide animation.
- Smoother CubicEase island transitions.
- Caps Lock, Num Lock, battery, and volume utility cards.
- Display picker for multi-monitor setups.
- Modern context menu and settings panel.
- Optional start with Windows.
- Adaptive top bar with active-app color sampling, network, volume, clock, and calendar controls.
- macOS-style dock with running/minimized apps, pinning, drag-to-reorder, and app actions.
- Searchable launcher that discovers installed desktop, Store, Riot, and Steam apps.
- Per-app launcher hiding for keeping the application list clean.

## Requirements

- Windows 10 19041 or newer.
- .NET 8 Desktop Runtime.

## macOS

macOS is not supported by this WPF build. WinDynamicIsland depends on Windows-only APIs such as WPF, GSMTC, Windows notifications, and Windows privacy indicators.

## Build

```powershell
dotnet build -c Release
```

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o release
```

Run `release\WinDynamicIsland.exe`.

## Settings

Right-click the island and open `Settings` to toggle notifications, system alerts, weather, screenshot preview, timer controls, start with Windows, and the target display.
