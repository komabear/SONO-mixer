; SONO Mixer — Inno Setup script
; Builds SONO-Setup.exe: installs to Program Files, Start-Menu shortcuts,
; optional launch-at-startup (HKCU Run, same value name as the in-app toggle),
; uninstaller. VAC is NOT bundled — the app's first-run wizard asks for the
; user's own VAC package and installs the driver from it.

#define MyAppName "SONO Mixer"
#define MyAppVersion "0.5.0"
#define MyAppPublisher "AndreYin"
#define MyAppExeName "SONO.App.exe"

[Setup]
AppId={{8F2A6D9C-4B7E-4C3A-9E5D-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SONO Mixer
DefaultGroupName=SONO Mixer
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=SONO-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=assets_installer\sono.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; autostart is handled by the app itself on first launch (HKCU Run "SONO") — safer than
; the installer writing HKCU while elevated (could land in a different user's hive).

[Files]
Source: "dist\SONO-Mixer\SONO.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\SONO-Mixer\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
