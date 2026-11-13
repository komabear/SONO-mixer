using System.Reflection;
using Microsoft.Win32;

namespace SONO.Core.SystemIntegrations;

/// <summary>Start-with-Windows via the per-user HKCU Run key. No admin rights needed.
/// The entry is written as <c>"path\SONO.App.exe" --autostart</c>: the argument is how SONO
/// tells "Windows started me at sign-in" from "the user opened me", which is what
/// "Start minimized to tray" depends on.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SONO";

    /// <summary>Passed by the Run entry; Program treats <c>/autostart</c> the same way.</summary>
    public const string AutostartArgument = "--autostart";

    /// <summary>True when the Run entry launches THIS exe (with or without the argument).</summary>
    public static bool IsEnabled()
    {
        var path = RegisteredExePath(out _);
        return path is not null && string.Equals(path, ExePath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath()}\" {AutostartArgument}");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Entries written by 3.3.2 and earlier are just the quoted path, so a sign-in
    /// start looked like any other start. If the entry is ours (it launches this exe) and
    /// lacks the argument, rewrite it with the argument. An entry that points at another
    /// copy of SONO is never touched. Returns true when the entry was rewritten.</summary>
    public static bool UpgradeEntry()
    {
        var path = RegisteredExePath(out bool hasArgument);
        if (path is null || hasArgument) return false;
        if (!string.Equals(path, ExePath(), StringComparison.OrdinalIgnoreCase)) return false;
        Set(true);
        return true;
    }

    /// <summary>The exe the Run entry launches (null when there is no entry), and whether
    /// the entry carries the autostart argument.</summary>
    private static string? RegisteredExePath(out bool hasArgument)
    {
        hasArgument = false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string command) return null;
        command = command.Trim();
        if (command.Length == 0) return null;

        string path, rest;
        if (command[0] == '"')
        {
            int close = command.IndexOf('"', 1);
            if (close < 0) { path = command.Trim('"'); rest = ""; }
            else { path = command[1..close]; rest = command[(close + 1)..]; }
        }
        else
        {
            // unquoted: the path may itself contain spaces, so only a trailing known
            // argument is split off
            path = command;
            rest = "";
            foreach (var arg in new[] { AutostartArgument, "/autostart" })
            {
                if (!command.EndsWith(" " + arg, StringComparison.OrdinalIgnoreCase)) continue;
                path = command[..^(arg.Length + 1)];
                rest = arg;
                break;
            }
        }

        foreach (var arg in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (arg.Equals(AutostartArgument, StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/autostart", StringComparison.OrdinalIgnoreCase))
                hasArgument = true;
        return path.Trim();
    }

    public static string ExePath()
        => Environment.ProcessPath
           ?? Assembly.GetEntryAssembly()?.Location
           ?? "";
}
