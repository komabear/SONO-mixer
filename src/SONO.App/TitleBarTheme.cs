using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SONO.App;

/// <summary>Windows follows the user's light/dark OS choice for title bars (Windows 10 1903+).
/// Every top-level form in SONO calls <see cref="Apply"/> on creation so its title bar
/// matches the system setting instead of staying default-white.</summary>
public static class TitleBarTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>True when Windows is in dark app mode (AppsUseLightTheme = 0).
    /// Defaults to dark when the registry key is missing (server SKUs).</summary>
    public static bool SystemPrefersDark
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
            }
            catch { return true; }
        }
    }

    /// <summary>Apply the OS light/dark choice to this form's title bar. Safe to call again
    /// (e.g. after a WM_SETTINGCHANGE if the user flips the OS theme while running).</summary>
    public static void Apply(Form form)
    {
        try
        {
            int dark = SystemPrefersDark ? 1 : 0;
            // attribute 20 on 20H1+; attribute 19 on older builds — set both, ignore failures
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref dark, sizeof(int));
        }
        catch
        {
            // pre-1903 Windows: title bar stays light — cosmetic only
        }
    }
}
