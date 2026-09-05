namespace SONO.Core.Audio;

public enum ChannelKind
{
    /// <summary>A channel: owns specific apps (group mode) and/or a playback device (routed mode).</summary>
    Group,
}

/// <summary>Hotkey slots per channel. Order matters: HotkeyAction casts to this.</summary>
public enum HotkeySlot
{
    VolDown,
    VolUp,
    Mute,
}

public sealed class ChannelDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Channel";
    public ChannelKind Kind { get; set; } = ChannelKind.Group;
    public string ColorHex { get; set; } = "#7aa2f7";

    /// <summary>Playback endpoint this channel captures (a VAC "Line N"). Null = session-ownership group mode.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Lower-case executable names assigned to this channel ("spotify.exe"). "system" = Windows sounds.</summary>
    public List<string> Executables { get; set; } = new();

    public float Volume { get; set; } = 1f;   // 0..1
    public bool Muted { get; set; }

    /// <summary>Hotkey text per slot ("VolDown" → "Ctrl+Alt+F1"); missing = unbound.</summary>
    public Dictionary<string, string?> Hotkeys { get; set; } = new(StringComparer.Ordinal);

    public string? GetHotkey(HotkeySlot slot)
        => Hotkeys.TryGetValue(slot.ToString(), out var v) ? v : null;

    public void SetHotkey(HotkeySlot slot, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) Hotkeys.Remove(slot.ToString());
        else Hotkeys[slot.ToString()] = text;
    }

    public bool OwnsExe(string exe)
        => Kind == ChannelKind.Group
           && Executables.Contains(exe, StringComparer.OrdinalIgnoreCase);
}
