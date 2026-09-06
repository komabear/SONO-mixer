; SONO Mixer — Inno Setup script
; Builds SONO-Setup.exe: installs to Program Files, Start-Menu shortcuts,
; optional desktop icon. VAC is NOT bundled — the app's first-run wizard asks
; for the user's own VAC package and installs the driver from it.
; On UNINSTALL, the user is asked (Yes-default) whether to also remove VAC;
; uninstall-vac.ps1 then removes driver/devices/settings (the script the
; manual cleanup battle-tested, incl. pt-BR pnputil parsing).

#define MyAppName "SONO Mixer"
#define MyAppVersion "0.5.0"
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
Source: "installer\uninstall-vac.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
var
  RemoveVac: Boolean;
  VacScriptCopy: String;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  RemoveVac := False;

  // 1) make sure SONO isn't running (a running app holds files and re-writes its
  //    autostart entry on exit paths): graceful close first, then hard kill
  Exec('taskkill.exe', '/IM SONO.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
  Exec('taskkill.exe', '/F /IM SONO.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 2) clear the per-user autostart entry (whatever exe path it points at)
  RegDeleteValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', 'SONO');

  // 3) offer VAC removal if there is something to remove.
  if FileExists('C:\Windows\System32\drivers\vrtaucbl.sys')
     or RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\VirtualAudioCable_83ed7f0e-2028-4956-b0b4-39c76fdaef1d') then
  begin
    // Yes is the default (checked-by-default semantics)
    RemoveVac :=
      MsgBox('Also remove Virtual Audio Cable?' #13#10 #13#10 +
             'This removes the VAC driver, its virtual devices and settings ' +
             '(the "SONO - ..." outputs). Audio will briefly stop.' #13#10 #13#10 +
             'Choose No to keep Virtual Audio Cable installed.',
             mbConfirmation, MB_YESNO) = IDYES;

    if RemoveVac then
    begin
      // the script gets deleted with {app}, so stash a copy for the post-uninstall step
      VacScriptCopy := ExpandConstant('{tmp}\uninstall-vac.ps1');
      if not FileCopy(ExpandConstant('{app}\uninstall-vac.ps1'), VacScriptCopy, False) then
      begin
        MsgBox('Could not stage the VAC removal script; VAC will be left installed.',
               mbError, MB_OK);
        RemoveVac := False;
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveVac then
  begin
    // uninstaller already runs elevated — no second UAC
    Exec('powershell.exe',
         '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + VacScriptCopy + '"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    DeleteFile(VacScriptCopy);

    if FileExists('C:\Windows\System32\drivers\vrtaucbl.sys') then
      MsgBox('Virtual Audio Cable removal finished.' #13#10 +
             'The driver file was still in use — a REBOOT will complete the removal.',
             mbInformation, MB_OK)
    else
      MsgBox('Virtual Audio Cable was removed.', mbInformation, MB_OK);
  end;
end;
