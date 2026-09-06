# SONO Mixer

A lightweight SteelSeries-Sonar-style audio mixer for Windows. While SONO runs, each of its
four channels — **Game / Chat / Media / Aux** — acts on its own virtual output device in
Windows: you point each app at a channel's device (one-time, per app), and SONO captures the
channels, applies volume/mute, and mixes everything back out of your real speakers or
headphones. Global hotkeys control each channel from anywhere.

Built with C# / WinForms / NAudio (WASAPI).

## How it works

```
YouTube Music ──▶ SONO - Media ─┐
Discord ────────▶ SONO - Chat ──┤  (per-channel volume/mute/hotkeys)
game.exe ───────▶ SONO - Game ──┤
                                └──▶ SONO Mixer ──▶ Fones de ouvido (your real output)
unassigned apps ───────────────────────────────────▶ play directly (Windows default)
```

- Channels bind to virtual devices **by name** (`SONO - Game`, `SONO - Chat`,
  `SONO - Media`, `SONO - Aux`), so driver reinstalls and device re-enumerations
  don't break the mapping.
- **Windows default output should stay on your real device** (e.g. your headphones).
  The SONO devices are destinations for *assigned* apps only — never set one as the
  Windows default (that would loop audio back into the mixer).
- Windows provides **no API** to set an app's output device programmatically (verified by
  reverse-engineering `AudioSes.dll`; the per-app choices are user-facing settings only).
  That's why the app assignment step is manual — SONO covers everything else, and its
  **routing health monitor** tells you whenever an assigned app is playing on the wrong
  device (status bar shows `⚠ N app(s) mis-routed — click to fix`).

## Requirements

- Windows 10 2004+ / Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  (or build from source — it's self-contained)
- [**Virtual Audio Cable 4.x**](https://vac.muzychenko.net/en/) — a third-party driver that
  provides the virtual devices. It is **not included** in this repository (see Licensing).

## Building & running

```
git clone https://github.com/AndreYin/SONO-Mixer.git
cd SONO-Mixer
dotnet build SONO.slnx -c Release
```

Then run:

```
src\SONO.App\bin\Release\net10.0-windows\SONO.App.exe
```

First launch: enable **Start with Windows** (⚙ menu) if you want it at boot; the window
lives in the tray when closed.

## One-time setup (per machine)

**1. Install Virtual Audio Cable** — SONO does not bundle VAC; each user uses their own
copy (free Trial works for testing; the full version removes the trial voice reminder,
$30–50 one-time). On first run (or via ⚙ → *Run automatic device setup…*), SONO asks for
the **unpacked VAC 4.x folder** (e.g. `D:\Downloads\Virtual Audio Cable 4.70`), validates
it (`vrtaucbl.inf`, `x64\vrtaucbl.sys`, signature catalog), and — after one administrator
approval — stages the driver (`pnputil`), sets **4 cables**, and re-enumerates.
A reboot is recommended after a fresh driver install.

**2. Set the cable count to 4.** Either via the VAC Control Panel (`Cables = 4`, Set) or by
writing the registry value used by SONO's own setup path:

```
reg add "HKLM\SOFTWARE\EuMus Design\Virtual Audio Cable\4" /v "Number of cables" /t REG_DWORD /d 4 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Services\VirtualAudioCable_83ed7f0e-2028-4956-b0b4-39c76fdaef1d\Parameters" /v "Number of cables" /t REG_DWORD /d 4 /f
```

then bounce the VAC device (Device Manager → disable/enable "Virtual Audio Cable") or reboot.

**3. Rename the four render endpoints** to `SONO - Game`, `SONO - Chat`, `SONO - Media`,
`SONO - Aux`. Their names live in the registry (Settings' "rename" UI doesn't exist for
these); each endpoint's key is
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\Properties`
— set values `{a45c254e-df1c-4efd-8020-67d146a850e0},2` and `...,14` to the name. These keys
are ACL-protected: take ownership (Administrators) first, write, then bounce the device.
(An in-app setup wizard that automates steps 2–3 with one elevation is planned.)

**4. Start SONO.** It finds the four devices by name and starts capturing.

## Daily use

- Point each app at its channel **once**: Windows Settings → System → Sound → Volume mixer
  → app → Output device → the app's `SONO - …`. It persists per app.
- Drag & drop apps between channels inside SONO anytime; the Windows-side choice must match
  the channel you want the app in.
- **OUTPUT picker** (top of the Applications panel): where the mixed channels play — usually
  your headphones/speakers.
- **Hotkeys**: click a shortcut box, press the combo (e.g. `Alt+7`), or press a bare
  multimedia key. Global — works while any app has focus. `✕` clears.
- Channel ⚙ menu: color, "Set device as Windows default" (only if you want that channel to
  also catch all unassigned apps — remember to switch your real output back afterwards).
- Status bar: shows the mix output, routing state, and mis-route warnings.

## Troubleshooting

- **Channel slider doesn't affect an app** → the app is playing on the wrong device. The
  status bar will say `⚠ N app(s) mis-routed`; click it for the exact list, then fix the
  app's Output in Windows Volume mixer.
- **After a VAC reinstall/driver bounce**, Windows may forget per-app choices — re-check
  them in Volume mixer. SONO will flag any drift.
- **No sound at all** → check `%APPDATA%\SONO\log.txt`; every routing change and error is
  logged there.

## Licensing notes

- This repository contains **no VAC binaries or license files**. Virtual Audio Cable is a
  commercial product by EuMus Design; each user installs their own copy (a feature-limited
  free tier exists; the full tier is paid). One BUSINESS license covers one running VAC
  instance.
- The Hatsune Miku mascot is fan art, included for personal use in this private repository.
