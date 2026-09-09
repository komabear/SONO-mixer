# SONO Mixer

A lightweight per-app audio mixer for Windows. Organize your apps into four groups —
**Game / Chat / Media / Aux** — and control each group's volume, mute, and global
hotkeys from one place. No drivers, no setup: install and use.

Built with C# / WinForms (.NET 10). Group volumes are applied through Windows' own
audio-session APIs — the same mechanism the system Volume mixer uses — so audio stays
bit-perfect with zero added latency.

## How it works

```
Discord ────────► CHAT group  (volume 0.70) ──┐
YouTube Music ───► MEDIA group (volume 0.43) ──┤   SONO applies each group's
Overwatch ───────► GAME group  (volume 1.00) ──┤   volume/mute to its apps'
                                               │   Windows audio sessions
all apps play straight to your normal output  ◄┘   (headphones/speakers)
```

- Drag any app onto a group card; its volume then follows that group's slider,
  mute button, and hotkeys — system-wide, even while a game has focus.
- Apps not in any group behave exactly as Windows normally handles them.
- **OUTPUT picker** (and the tray menu's Output submenu) switches the *Windows default
  output* system-wide — every app on "default" follows, AudioSwitch-style.
- The Applications panel lists currently-open audio apps (live volume meters,
  per-app mute) — refreshed the moment SONO gets focus.

## Features

- **4 fixed groups** — Game / Chat / Media / Aux, themed and color-coded
- **Group volume / mute**, live meters, per-app mute in the Applications list
- **Global hotkeys** — any key combo or bare multimedia keys, per group:
  Vol− / Vol+ / Mute, with an adjustable step size
- **Volume OSD** — Sonar-style popup on hotkey presses (9 screen positions + off),
  never steals focus, click-through, themed
- **System output switching** — from the app or the tray, with the current default
  check-marked
- **Themes** — a dozen palettes (incl. Hatsune Miku), follow the app instantly
- **Tray-first** — lives in the tray, starts with Windows, single-click to open

## Requirements

- Windows 10 2004+ / Windows 11 (x64)
- To **run the release installer**: nothing else (self-contained).
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Setup — step by step

1. **Install** — download `SONO-Setup.exe` from
   [Releases](https://github.com/komabear/SONO-mixer/releases) and run it. That's the
   whole setup: no drivers, no audio devices, no restarts.
2. **Assign apps** — start an app (play something), it appears in the Applications
   panel; drag it onto a group. Done — its volume now follows that group.
3. **Optional: hotkeys** — click a shortcut box on a group card and press any combo
   (e.g. `Alt+7`) or a bare media key. Works system-wide; `✕` clears.
4. **Mix** — sliders, mutes, hotkeys, OSD. The OUTPUT picker switches your system
   output whenever you need to.

## Building from source

```
git clone https://github.com/komabear/SONO-mixer.git
cd SONO-mixer
dotnet build SONO.slnx -c Release
```

Executable: `src\SONO.App\bin\Release\net10.0-windows\SONO.App.exe`
Installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)): compile
`installer.iss` → `dist\SONO-Setup.exe`.

## Troubleshooting

- **A group slider doesn't affect an app** → the app has no live audio session yet
  (it only appears once it makes sound), or another app/window owns its session.
  Play something in it and check the Applications panel.
- **Volume changes feel slow** → SONO applies group volumes continuously (sub-second);
  if Windows' own mixer fights it, an app was manually set — release it there.
- **Anything else** → check `%APPDATA%\SONO\log.txt`; every error is logged there.

## History note

SONO previously supported a "virtual devices" mode built on Virtual Audio Cable
(per-group virtual outputs + loopback mixing). It was removed in favor of the simpler,
driver-free group-volume model — no VAC license, no added latency, no drift/dropout
class of bugs. The old implementation lives in the git history.
