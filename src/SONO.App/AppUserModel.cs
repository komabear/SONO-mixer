using System;
using System.Runtime.InteropServices;

/// <summary>
/// Sets an explicit AppUserModelID so the taskbar always groups/pins SONO under one
/// identity and reads its icon from a STABLE source (the shortcut/exe), instead of
/// re-resolving a re-published exe's icon through Explorer's cache.
/// </summary>
public static class AppUserModel
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    public static void Set(string id)
    {
        try { SetCurrentProcessExplicitAppUserModelID(id); } catch { /* cosmetic only */ }
    }
}
