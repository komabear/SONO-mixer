using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SONO.Core.SystemIntegrations;

/// <summary>
/// Writes Windows' per-app default-endpoint store
/// (HKCU\Software\Microsoft\Multimedia\Audio\DefaultEndpoint\{hash}_0) so an app's audio
/// lands on a chosen endpoint the next time it creates an audio session.
///
/// Windows exposes no public API for per-app output; this store is what the Volume-mixer
/// setting writes. Undocumented but stable for a decade+; the key name is Windows'
/// string hash of the app's NT device path (h = h*33 + codepoint, seed 0) — verified
/// against real entries. All values are computed at runtime; nothing machine-specific
/// is hardcoded.
/// </summary>
public static class AppEndpointStore
{
    private const string StoreKey = @"Software\Microsoft\Multimedia\Audio\DefaultEndpoint";
    private const string RenderFlowSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, System.Text.StringBuilder lpTargetPath, int ucchMax);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Windows' hash of an NT path → the 8-hex key-name prefix.
    /// Formula verified against 11 real store entries on Win11 build 26200.</summary>
    internal static string HashPath(string ntPath)
    {
        uint h = 0;
        foreach (var ch in ntPath) h = h * 33 + ch;
        return $"{h:x8}";
    }

    /// <summary>Full NT device path (\Device\HarddiskVolumeN\…) of a process, or null.
    /// Computed live via QueryFullProcessImageName + QueryDosDevice — machine-independent.</summary>
    public static string? NtImagePath(int pid)
    {
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = 1024;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero || !QueryFullProcessImageName(h, 0, sb, ref size)) { if (h != IntPtr.Zero) CloseHandle(h); return null; }
            var winPath = sb.ToString();          // e.g. C:\Users\...\app.exe
            CloseHandle(h);
            var drive = winPath[..2];             // "C:"
            var rest = winPath[2..];              // \Users\...
            var nt = new System.Text.StringBuilder(512);
            if (QueryDosDevice(drive, nt, nt.Capacity) == 0) return null;
            return nt.ToString() + rest;          // \Device\HarddiskVolume3\Users\...
        }
        catch { return null; }
    }

    /// <summary>Point every future audio session of the process at the given endpoint.
    /// Takes effect when the app next creates a session (usually its next restart).
    /// No service restart needed — verified empirically.</summary>
    public static bool SetAppEndpoint(int pid, string endpointId)
    {
        var ntPath = NtImagePath(pid);
        return ntPath is not null && SetAppEndpointByPath(ntPath, endpointId);
    }

    /// <summary>Same, for a known NT path (used when the process isn't running).</summary>
    public static bool SetAppEndpointByPath(string ntPath, string endpointId)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($"{StoreKey}\\{HashPath(ntPath)}_0");
            if (key is null) return false;

            // value format observed in sibling entries: SWD MMDEVAPI wrapper + endpoint + render-flow suffix
            string ep = $"\\\\?\\SWD#MMDEVAPI#{endpointId}{RenderFlowSuffix}";

            // reuse the app's own flow GUID if it has one (device-flow identity), else the
            // common render GUID seen in every observed key
            string flow = "{FD5F2F0A-FD72-4C45-ADD3-8A783A9E3465}";
            try
            {
                var own = (string?)key.GetValue("000_000_p");
                if (!string.IsNullOrEmpty(own)) flow = own;
            }
            catch { }

            key.SetValue(null, ntPath);                       // (default) = app path
            key.SetValue("000_000_p", flow);                  // console role
            key.SetValue("000_000", ep);
            key.SetValue("001_000_p", flow);                  // multimedia role
            key.SetValue("001_000", ep);
            return true;
        }
        catch (Exception ex)
        {
            SONO.Core.Diagnostics.Log.Write($"AppEndpointStore write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The endpoint the store currently assigns to a process, or null (= system default).</summary>
    public static string? GetAppEndpoint(int pid)
    {
        try
        {
            var ntPath = NtImagePath(pid);
            if (ntPath is null) return null;
            using var key = Registry.CurrentUser.OpenSubKey($"{StoreKey}\\{HashPath(ntPath)}_0");
            var raw = (string?)key?.GetValue("001_000");
            // strip the SWD wrapper: ...#{endpoint}#suffix → endpoint
            if (raw is null) return null;
            int s = raw.IndexOf("#{0.", StringComparison.Ordinal);
            int e = raw.LastIndexOf('}');
            return s > 0 && e > s ? raw.Substring(s + 1, e - s - 1) : null;
        }
        catch { return null; }
    }
}
