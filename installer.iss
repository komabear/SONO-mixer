; SONO Mixer — Inno Setup script
; Builds SONO-Setup.exe: installs to Program Files, Start-Menu shortcuts,
; optional desktop icon. No audio driver needed — SONO controls app volumes
; through Windows' own session APIs.

#define MyAppName "SONO Mixer"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "komabear"
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
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=installer\sono.ico

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

[Code]
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  // make sure SONO isn't running (it holds files and its session controls)
  Exec('taskkill.exe', '/IM SONO.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
  Exec('taskkill.exe', '/F /IM SONO.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // clear the per-user autostart entry (whatever exe path it points at)
  RegDeleteValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', 'SONO');
end;
