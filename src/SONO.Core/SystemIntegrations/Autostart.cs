using System.Reflection;
using Microsoft.Win32;

namespace SONO.Core.SystemIntegrations;

/// <summary>Start-with-Windows via the per-user HKCU Run key. No admin rights needed.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SONO";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && string.Equals(v.Trim('"'), ExePath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath()}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static string ExePath()
        => Environment.ProcessPath
           ?? Assembly.GetEntryAssembly()?.Location
           ?? "";
}
