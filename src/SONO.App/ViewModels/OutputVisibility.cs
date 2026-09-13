using SONO.Core.Settings;

namespace SONO.App.ViewModels;

/// <summary>Per-device output visibility, persisted in settings (hidden output ids).</summary>
public static class OutputVisibility
{
    /// <summary>True when the given endpoint is visible on the main window dropdown.
    /// Unknown devices default to visible; the always-current default device is forced visible.</summary>
    public static bool IsVisible(AppSettings settings, string deviceId)
    {
        if (string.Equals(deviceId, settings.RealOutputId, StringComparison.OrdinalIgnoreCase))
            return true;   // the active output can never be hidden from the dropdown
        return !settings.HiddenOutputs.Contains(deviceId, StringComparer.OrdinalIgnoreCase);
    }

    public static void SetVisible(AppSettings settings, string deviceId, bool visible)
    {
        if (visible) settings.HiddenOutputs.Remove(deviceId);
        else settings.HiddenOutputs.Add(deviceId);
    }
}
