# Xbox Battery Tray

A small Windows tray app that shows your Xbox controller's battery level in the notification area (bottom right of the taskbar).

## Features

- **Battery percentage in the tray icon**, colored by level (green > 50%, yellow > 20%, red ≤ 20%) with a fill bar. A green controller icon means fully charged.
- **Time remaining estimate** – learns how fast your battery drains while the controller is on (time it's switched off doesn't count) and remembers it between restarts.
- **Charging over USB** – shows a lightning bolt, the charge level and an estimated time until full, plus a notification when it's fully charged.
- **Low battery notification** – once per discharge, at 10% by default (5–25% or off, from the right-click menu).
- **Last known level** shown in grey when the controller is off.
- **Start with Windows** option in the right-click menu.

Works with Xbox controllers over Bluetooth, USB, and the Xbox Wireless Adapter (the adapter only reports four coarse levels).

## Usage

Run `XboxBatteryTray.exe`. Hover over the icon for details, left-click to refresh, right-click for options.

## Building

No SDK needed – it builds with the C# compiler that ships with Windows (.NET Framework 4.x):

```
build.cmd
```

This produces `XboxBatteryTray.exe` next to the source, with `xbox-controller.png` embedded.

## How it reads the battery

1. **Windows.Gaming.Input** battery report – used over USB, where it also gives the charge status.
2. **Bluetooth device property** – the level Windows shows in Settings → Bluetooth & devices; used for Bluetooth controllers and for the "last known" level. (Windows.Gaming.Input misreports Bluetooth controllers, so it isn't used for them.)
3. **Windows.Gaming.Input / XInput** – fallback for the Xbox Wireless Adapter.

Settings and the learned drain/charge rates are stored under `HKCU\Software\XboxBatteryTray`.
