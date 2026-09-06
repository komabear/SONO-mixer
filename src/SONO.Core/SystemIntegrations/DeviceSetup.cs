using NAudio.CoreAudioApi;
using SONO.Core.Diagnostics;

namespace SONO.Core.SystemIntegrations;

/// <summary>
/// One-elevation device setup: ensures 4 VAC cables, renames the endpoints to
/// SONO - Game/Chat/Media/Aux (registry, ACL take-ownership included), and bounces
/// the device. All steps were proven individually; this wraps them in one script.
/// </summary>
public static class DeviceSetup
{
    public static bool VacServicePresent => File.Exists(@"C:\Windows\System32\drivers\vrtaucbl.sys");

    public sealed record EndpointInfo(string Id, string FriendlyName);

    public sealed record VacPackageValidation(
        bool Valid, string InfPath, List<string> Found, List<string> Missing, string Details);

    /// <summary>VAC render endpoints found on the system ("Line N (Virtual Audio Cable)").</summary>
    public static List<EndpointInfo> VacRenderEndpoints()
    {
        try
        {
            return new MMDeviceEnumerator()
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Where(d => d.FriendlyName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase))
                .Select(d => new EndpointInfo(d.ID, d.FriendlyName))
                .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    public static bool SonoEndpointsPresent()
    {
        try
        {
            return new MMDeviceEnumerator()
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Any(d => d.FriendlyName.StartsWith("SONO - ", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Validate a user-supplied VAC distribution folder (e.g. "…\Virtual Audio Cable 4.70").
    /// Accepts the standard package layout (INF at root or inside x64\). We never bundle VAC —
    /// the user supplies their own lawfully obtained copy and SONO installs from it.</summary>
    public static VacPackageValidation ValidateVacPackage(string dir)
    {
        var found = new List<string>();
        var missing = new List<string>();

        if (!Directory.Exists(dir))
            return new VacPackageValidation(false, "", found, new List<string> { "folder does not exist" },
                $"Folder not found:\n{dir}");

        string? inf = null, sys = null, cat = null;

        foreach (var cand in new[] { "vrtaucbl.inf", Path.Combine("x64", "vrtaucbl.inf"), Path.Combine("x86", "vrtaucbl.inf") })
            if (File.Exists(Path.Combine(dir, cand))) { inf = Path.Combine(dir, cand); break; }
        foreach (var cand in new[] { Path.Combine("x64", "vrtaucbl.sys"), "vrtaucbl.sys" })
            if (File.Exists(Path.Combine(dir, cand))) { sys = Path.Combine(dir, cand); break; }
        foreach (var cand in new[] { "vrtaucbl.cat", "vrtaucbl6x.cat", Path.Combine("x64", "vrtaucbl.cat") })
            if (File.Exists(Path.Combine(dir, cand))) { cat = Path.Combine(dir, cand); break; }

        if (inf is not null) found.Add(inf); else missing.Add("vrtaucbl.inf (driver INF)");
        if (sys is not null) found.Add(sys); else missing.Add("x64\\vrtaucbl.sys (driver binary)");
        if (cat is not null) found.Add(cat); else missing.Add("vrtaucbl*.cat (signature catalog)");

        var details = missing.Count == 0
            ? $"VAC package OK:\n  {inf}\n  {sys}\n  {cat}"
            : "Missing required files:\n  " + string.Join("\n  ", missing) +
              "\n\nPoint this at the unpacked Virtual Audio Cable 4.x distribution folder\n" +
              "(it must contain vrtaucbl.inf and an x64\\vrtaucbl.sys).";
        return new VacPackageValidation(missing.Count == 0, inf ?? "", found, missing, details);
    }

    /// <summary>Elevated phase-1 script for a FRESH install: stage the driver from the user's
    /// package (pnputil), set 4 cables, bounce. Endpoint renames happen in phase 2 once the
    /// new endpoints exist (BuildScript).</summary>
    public static string BuildDriverInstallScript(string infFullPath)
    {
        return $@"$ErrorActionPreference = 'Continue'
$log = Join-Path $env:LOCALAPPDATA 'Temp\sono_setup.log'
'=== SONO driver install ===' | Out-File $log

pnputil /add-driver ""{infFullPath}"" /install 2>&1 | Out-File $log -Append

reg add ""HKLM\SOFTWARE\EuMus Design\Virtual Audio Cable\4"" /v ""Number of cables"" /t REG_DWORD /d 4 /f | Out-Null
reg add ""HKLM\SYSTEM\CurrentControlSet\Services\VirtualAudioCable_{VacServiceGuid}\Parameters"" /v ""Number of cables"" /t REG_DWORD /d 4 /f | Out-Null
'cable count set to 4' | Out-File $log -Append

try {{
  $d = Get-PnpDevice | Where-Object {{ $_.InstanceId -like 'ROOT\{{{VacServiceGuid}*' }} | Select-Object -First 1
  if ($d) {{
    Disable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false
    Start-Sleep 2
    Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false
    'device bounced' | Out-File $log -Append
  }} else {{ 'no device node yet (will appear on first use)' | Out-File $log -Append }}
}} catch {{ ('bounce failed: ' + $_.Exception.Message) | Out-File $log -Append }}
'=== DONE ===' | Out-File $log -Append";
    }

    private static readonly string[] ChannelNames = { "Game", "Chat", "Media", "Aux" };
    private const string VacServiceGuid = "83ed7f0e-2028-4956-b0b4-39c76fdaef1d";

    /// <summary>Generate the elevated PowerShell script for the current endpoint set.</summary>
    public static string BuildScript(IReadOnlyList<EndpointInfo> endpoints)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$log = Join-Path $env:LOCALAPPDATA 'Temp\\sono_setup.log'");
        sb.AppendLine("'=== SONO device setup ===' | Out-File $log");

        sb.AppendLine(@"Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Priv {
  [DllImport(""advapi32.dll"", SetLastError=true)]
  static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
  [DllImport(""advapi32.dll"", SetLastError=true)]
  static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
  [DllImport(""advapi32.dll"", SetLastError=true)]
  static extern bool AdjustTokenPrivileges(IntPtr tok, bool dis, ref TOKPRIV1LUID newst, int len, IntPtr prev, IntPtr ret);
  [StructLayout(LayoutKind.Sequential, Pack=1)]
  struct TOKPRIV1LUID { public int Count; public long Luid; public int Attr; }
  public static void Enable(string privilege) {
    IntPtr tok;
    OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out tok);
    TOKPRIV1LUID tp; tp.Count = 1; tp.Attr = 2;
    LookupPrivilegeValue(null, privilege, out tp.Luid);
    AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
  }
}
'@
[Priv]::Enable('SeTakeOwnershipPrivilege')
[Priv]::Enable('SeBackupPrivilege')
[Priv]::Enable('SeRestorePrivilege')");

        // cable count (both registry branches)
        sb.AppendLine(@"reg add ""HKLM\SOFTWARE\EuMus Design\Virtual Audio Cable\4"" /v ""Number of cables"" /t REG_DWORD /d 4 /f | Out-Null
reg add ""HKLM\SYSTEM\CurrentControlSet\Services\VirtualAudioCable_" + VacServiceGuid + @"\Parameters"" /v ""Number of cables"" /t REG_DWORD /d 4 /f | Out-Null");

        // renames: map endpoints (sorted) to channel names
        for (int i = 0; i < endpoints.Count && i < ChannelNames.Length; i++)
        {
            var guid = endpoints[i].Id.Split('.').Last().Trim('{', '}');
            var name = $"SONO - {ChannelNames[i]}";
            var path = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render\\{{{guid}}}\\Properties";
            sb.AppendLine($@"try {{
  $path = '{path}'
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubtree, [System.Security.AccessControl.RegistryRights]::TakeOwnership)
  $acl = $key.GetAccessControl([System.Security.AccessControl.AccessControlSections]::Owner)
  $acl.SetOwner($admins); $key.SetAccessControl($acl); $key.Close()
  $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubtree, [System.Security.AccessControl.RegistryRights]::ChangePermissions)
  $acl = $key.GetAccessControl()
  $rule = New-Object System.Security.AccessControl.RegistryAccessRule($admins, 'FullControl', 'ContainerInherit', 'None', 'Allow')
  $acl.SetAccessRule($rule); $key.SetAccessControl($acl); $key.Close()
  $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, $true)
  $key.SetValue('{{a45c254e-df1c-4efd-8020-67d146a850e0}},2', '{name}', [Microsoft.Win32.RegistryValueKind]::String)
  $key.SetValue('{{a45c254e-df1c-4efd-8020-67d146a850e0}},14', '{name}', [Microsoft.Win32.RegistryValueKind]::String)
  $key.SetValue('{{b3f8fa53-0004-438e-9003-51a46e139bfc}},6', 'Virtual Audio Cable', [Microsoft.Win32.RegistryValueKind]::String)
  $key.Close()
  '{name.Replace("'", "''")}: OK' | Out-File $log -Append
}} catch {{ ('{name.Replace("'", "''")}: FAILED ' + $_.Exception.Message) | Out-File $log -Append }}");
        }

        // bounce the VAC device so names take effect
        sb.AppendLine(@"try {
  $d = Get-PnpDevice | Where-Object { $_.InstanceId -like 'ROOT\{" + VacServiceGuid + @"*' } | Select-Object -First 1
  Disable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false
  Start-Sleep 2
  Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false
  Start-Sleep 3
  'device bounced' | Out-File $log -Append
} catch { ('bounce failed: ' + $_.Exception.Message) | Out-File $log -Append }
'=== DONE ===' | Out-File $log -Append");
        return sb.ToString();
    }

    /// <summary>Write + run the script elevated, wait, and return the log text.</summary>
    public static string RunElevated(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "sono_setup.ps1");
        File.WriteAllText(scriptPath, script);
        var logPath = Path.Combine(Path.GetTempPath(), "sono_setup.log");
        try { File.Delete(logPath); } catch { }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            Verb = "runas",       // triggers UAC
            UseShellExecute = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(120_000);
        try { return File.ReadAllText(logPath); } catch { return "(no log produced)"; }
    }
}
