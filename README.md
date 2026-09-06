<img width="524" alt="image" src="https://github.com/user-attachments/assets/82d610fb-a374-4421-b5bd-e3221a277730" />


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
                                └──▶ SONO Mixer ──▶ Headphones / Speakers (your real output)
unassigned apps ──────────────────────────────────────────────▶ play directly (Windows default)
```

- Channels bind to virtual devices **by name** (`SONO - Game`, `SONO - Chat`,
  `SONO - Media`, `SONO - Aux`), so driver reinstalls and device re-enumerations
  don't break the mapping.
- The **Windows default output stays on your real device**. After device setup SONO
  automatically sets `SONO - Game` as default — that's intentional: Game is the
  catch-all channel for unassigned apps, and the mixer plays the sum out of your real
  output (pick it in the OUTPUT picker).
- Windows provides **no API** to set an app's output device programmatically (verified by
  reverse-engineering `AudioSes.dll`; the per-app choices are user-facing settings only).
  That's why the app assignment step is manual — SONO covers everything else, and its
  **routing health monitor** tells you whenever an assigned app is playing on the wrong
  device (status bar shows `⚠ N app(s) mis-routed — click to fix`).

## Requirements

- Windows 10 2004+ / Windows 11 (x64)
- [**Virtual Audio Cable 4.x (full version, license required)**](https://vac.muzychenko.net/en/purchase.htm) —
  a third-party driver that provides the virtual devices. It is **not included** in this
  repository (see Licensing). You only need the **downloaded full-package folder** —
  SONO's wizard installs and configures it.
- To **run the release installer**: nothing else (self-contained).
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Setup — step by step (fresh machine, ~5 minutes)

### 1. Install SONO Mixer

Download `SONO-Setup.exe` from [Releases](https://github.com/komabear/sono-mixer/releases)
(or build from source, below) and run it. It installs to `Program Files\SONO Mixer`, adds
Start-Menu shortcuts and an optional desktop icon. SONO starts with Windows and lives in
the tray by default.

### 2. Buy a Virtual Audio Cable license

SONO needs the **full version** of [Virtual Audio Cable](https://vac.muzychenko.net/en/) —
the free trial only provides **1 cable**, and SONO requires **4** (Game / Chat / Media / Aux).
Purchase a license at the
[official purchase page](https://vac.muzychenko.net/en/purchase.htm):

- **Home license ($30)** — enough for personal use, "not associated with income generation"
- **Business license ($50)** — if you use it commercially

The license is **one-time and perpetual** — no subscription. Volume discounts exist
(2+ licenses), plus 30–50% discounts for students and educational/non-profit
organizations. After purchase you'll get download instructions for the **full** package
(file name ends with `full`, e.g. `vac464full`) — that's the folder you'll point SONO at
in the next step.

### 3. Let SONO install & configure Virtual Audio Cable

SONO does **not** bundle VAC — it uses **your own downloaded copy**. On first launch a
wizard appears:

1. Click **Yes** when offered the automatic device setup (or later: ⚙ →
   *Run automatic device setup…*)
2. Point the wizard at your **unpacked VAC 4.x full-package folder** (e.g.
   `D:\Downloads\Virtual Audio Cable 4.70`). *Install* unlocks only after validation
   (`vrtaucbl.inf`, `x64\vrtaucbl.sys`, signature catalog).
3. Approve the administrator prompt. The wizard then:
   - stages the driver and **creates the virtual device**
   - sets the cable count to **4**
   - renames the endpoints to `SONO - Game / Chat / Media / Aux`
   - **hides the input ("Line N") side** — you only get the 4 outputs
   - sets **SONO - Game** as the Windows default output
   - bounces the device so everything takes effect (audio stops for a few seconds — normal)

A progress dialog narrates each step; a summary dialog shows the log at the end.
A reboot after the very first driver install is recommended.

### 4. Assign apps to channels (one-time, per app)

Windows decides where each app plays — no API exists to change it from software, so this
is the one manual step:

1. Open **Windows Settings → System → Sound → Volume mixer**
2. For each app set **Output** to its channel: `Discord → SONO - Chat`,
   `Spotify/YouTube Music → SONO - Media`, games → `SONO - Game`, …
3. Done — the choice persists per app. Unassigned apps play on `SONO - Game` (the default
   channel device) and still get Game's volume/hotkeys.

SONO's status bar warns `⚠ N app(s) mis-routed` whenever an assigned app is on the wrong
device — click it to see which.

### 5. Mix

- **Sliders / mute** per channel; per-app faders live in the Applications panel
- **Global hotkeys**: click a shortcut box on a channel card, press any combo
  (e.g. `Alt+7`) or a bare multimedia key — works system-wide, `✕` clears.
  Vol− / Vol+ / Mute per channel; step size adjustable (⚙ → *Hotkey volume step…*)
- **OUTPUT picker** (top of the Applications panel): where the mixed channels play —
  usually your headphones/speakers
- **Themes**: ⚙ → *Theme* — a dozen palettes including Hatsune Miku (with her own logo)
- SONO sits in the tray when closed; *Start with Windows* is on by default

## Building from source

```
git clone https://github.com/komabear/sono-mixer.git
cd sono-mixer
dotnet build SONO.slnx -c Release
```

Executable: `src\SONO.App\bin\Release\net10.0-windows\SONO.App.exe`
Installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)): compile
`installer.iss` → `dist\SONO-Setup.exe`.

## Uninstalling

"Uninstall SONO Mixer" (Start menu) removes the app. It then asks whether to **also remove
Virtual Audio Cable** (driver, virtual devices and settings) — default **Yes**. If the
driver file was still in use, a reboot completes the removal. The per-app Windows output
choices become harmless "missing device" entries that Windows cleans up on its own.

## Troubleshooting

- **Channel slider doesn't affect an app** → the app is playing on the wrong device. The
  status bar shows `⚠ N app(s) mis-routed`; click it for the exact list, then fix the
  app's Output in Volume mixer.
- **After a VAC reinstall/driver bounce**, Windows may forget per-app choices — re-check
  them in Volume mixer. SONO flags any drift.
- **Setup failed / devices missing** → re-run ⚙ → *Run automatic device setup…*; the
  result dialog (and `%LOCALAPPDATA%\Temp\sono_setup.log`) shows exactly which step
  failed.
- **No sound at all** → check `%APPDATA%\SONO\log.txt`; every routing change and error is
  logged there.
- **Weird volume/quality after changing themes in older builds** → fixed in current
  (the audio engine is now shared for the whole app lifetime). Update.

## Licensing notes

- This repository contains **no VAC binaries or license files**. Virtual Audio Cable is a
  commercial product by EuMus Design; each user purchases their own license
  ([purchase page](https://vac.muzychenko.net/en/purchase.htm) — Home $30 / Business $50,
  one-time). SONO never distributes VAC — it only installs **from the user's own package**.
- The Hatsune Miku artwork is fan art, included for personal use.
