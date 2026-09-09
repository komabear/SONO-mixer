# SONO Mixer

A lightweight per-app audio mixer for Windows. Organize your apps into four groups —
**Game / Chat / Media / Aux** — and control each group's volume, mute, and global
hotkeys from one place. No drivers, no extra audio devices, no setup: install and use.

Built with C# / WinForms (.NET 10). Group volumes are applied through Windows' own
audio-session APIs — the same mechanism the system Volume mixer uses — so audio stays
bit-perfect with zero added latency.

## How it works

```
Discord ────────► CHAT group  (volume 0.70) ──┐
YouTube Music ───► MEDIA group (volume 0.43) ──┤   SONO continuously applies each
Overwatch ───────► GAME group  (volume 1.00) ──┤   group's volume/mute to its
                                               │   apps' Windows audio sessions
all apps play straight to your normal output  ◄┘
```

- Drag any app onto a group card; its volume then follows that group's slider,
  mute button, and hotkeys — system-wide, even while a game has focus.
- Apps not in any group behave exactly as Windows normally handles them.
- The Applications panel lists currently-open audio apps with **live volume meters**
  (color-tinted to their group), refreshed the moment SONO gets focus.
- The **OUTPUT picker** (and the tray menu's Output submenu) switches the *Windows
  default output* system-wide — every app on "default" follows along.

## Features

- **4 fixed groups** — Game / Chat / Media / Aux, themed and color-coded
- **Group volume / mute** with live dB tooltips; per-app meters and per-app mute
  in the Applications list
- **Global hotkeys** — any key combo or bare multimedia keys, per group:
  Vol− / Vol+ / Mute, adjustable step size (⚙ → *Hotkey volume step…*)
- **Volume OSD** — Sonar-style popup on hotkey presses: channel name, percentage,
  and a channel-colored volume bar. Never steals focus, click-through, topmost;
  9 screen positions + Off (⚙ → *Volume popup position*)
- **System output switching** — from the app or the tray, current default
  check-marked, device list rebuilt fresh on every open
- **Themes** — a dozen palettes including Hatsune Miku (with her own logo);
  the default SONO logo shows on all other themes
- **Tray-first** — lives in the tray, single left-click opens the mixer,
  right-click menu: Open / Mute all / Unmute all / Output / Exit
- **About dialog** — version and repo link (⚙ → *About SONO Mixer*)
- Starts with Windows by default (toggle in ⚙); runs minimized to tray on boot

## Requirements

- Windows 10 2004+ / Windows 11 (x64)
- To **run the release installer**: nothing else (self-contained).
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Setup — step by step

1. **Install** — download
   [`SONO-Setup.exe`](https://github.com/komabear/SONO-mixer/releases/download/v1.1.0/SONO-Setup.exe)
   from [Releases](https://github.com/komabear/SONO-mixer/releases) and run it. That's the
   whole setup: no drivers, no audio devices, no restarts, no admin prompts.
2. **Assign apps** — start an app and play something; it appears in the Applications
   panel. Drag it onto a group card (or use the card's **+ add** picker). Done —
   its volume now follows that group.
3. **Optional: hotkeys** — click a shortcut box on a group card and press any combo
   (e.g. `Alt+7`) or a bare media key. Works system-wide; `✕` clears.
4. **Mix** — sliders, mutes, hotkeys, OSD. Switch your system output from the
   OUTPUT picker or the tray menu whenever you need to.

## Building from source

```
git clone https://github.com/komabear/SONO-mixer.git
cd SONO-mixer
dotnet build SONO.slnx -c Release
```

Executable: `src\SONO.App\bin\Release\net10.0-windows\SONO.App.exe`

Installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):
compile `installer.iss` → `dist\SONO-Setup.exe`.

## Uninstalling

"Uninstall SONO Mixer" (Start menu or Windows Settings → Apps) removes the app
completely: files, shortcuts, and the start-with-Windows entry. Nothing is left
behind — SONO installs no drivers and touches no system audio settings.

## Troubleshooting

- **A group slider doesn't affect an app** → the app has no live audio session yet
  (apps appear once they make sound). Play something in it and check the
  Applications panel.
- **A manually-set app volume keeps snapping back** → that app is in a group;
  SONO enforces the group's volume on it. Remove it from the group (✕ on its
  chip) if you want to control it independently.
- **Anything else** → check `%APPDATA%\SONO\log.txt`; every error is logged there.

## History note

SONO previously supported a "virtual devices" mode built on Virtual Audio Cable
(per-group virtual outputs + loopback capture/mixing, Sonar-style). It was removed
in favor of the simpler, driver-free group-volume model — no third-party license,
no added latency, and an entire class of drift/dropout bugs eliminated by design.
The old implementation lives in the git history.
