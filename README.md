# SONO Mixer

A lightweight SteelSeries-Sonar-style audio mixer for Windows: split your apps into four
channels (**Game / Chat / Media / Aux**), route each channel through its own virtual audio
device, mix everything back into your real output, and control it all with global hotkeys.

Built with C# / WinForms / NAudio (WASAPI).

## Features

- **4 fixed channels**, each bound to a virtual audio device by name (`SONO - Game`, …)
- **Real routing**: per-channel WASAPI loopback capture → gain/mute → mix → your real output
- **OUTPUT picker**: choose which device the mixed channels play through (cables excluded to prevent feedback loops)
- **Global hotkeys** per channel (Vol−, Vol+, Mute), system-wide, including bare multimedia keys
- **Per-app volumes** with channel-color tinting; drag & drop app routing
- **10 material themes** with instant hot-swap (audio keeps playing), incl. a Hatsune Miku theme
- **Adaptive layout** (4-up ↔ 2×2 grid, compact mode at half screen height)
- Tray integration, start-with-Windows, diagnostic log at `%APPDATA%\SONO\log.txt`

## Build

```
dotnet build SONO.slnx -c Release
```

Runs on .NET 10 (Windows desktop). The executable lands in
`src/SONO.App/bin/Release/net10.0-windows/SONO.App.exe`.

## Virtual audio devices (required for routing)

Windows has no user-mode API to create audio endpoints, so SONO uses
[**Virtual Audio Cable** (VAC 4.x)](https://vac.muzychenko.net/en/) as an **optional,
user-installed dependency**. This repository deliberately contains **no VAC binaries,
drivers, or license files** — the VAC license only permits distribution of the full
version together with its own lawfully obtained license, so each user installs it themselves.

Setup on a fresh machine:

1. Install VAC (the driver `vrtaucbl.sys` + service — the standard `setup64.exe` does this)
2. Set the cable count to **4** (VAC Control Panel → Cables = 4, or registry
   `HKLM\SOFTWARE\EuMus Design\Virtual Audio Cable\4` → `Number of cables` = 4, both
   branches, then bounce the device)
3. Name the four render endpoints `SONO - Game`, `SONO - Chat`, `SONO - Media`, `SONO - Aux`
   (endpoint names live in the registry under `MMDevices\Audio\Render\{guid}\Properties`,
   properties `{a45c254e-df1c-4efd-8020-67d146a850e0},2` / `,14`; those keys are
   ACL-protected — take ownership as admin first). An in-app setup helper is planned.
4. Start SONO — it binds channels to endpoints **by name**, so reinstalls/re-enumerations
   survive automatically

Channels without a matching device still work in **group mode** (they own their assigned
apps' session volumes directly, no isolation).

Without VAC the app still functions as a session-volume mixer; with VAC you get true
per-channel devices and mixing, Sonar-style.

## Notes

- The Miku mascot image is fan art included for personal use in this private repo.
- VAC licensing: one BUSINESS license covers one running VAC instance (your machines);
  the free/trial tier works too but injects a periodic voice reminder.
