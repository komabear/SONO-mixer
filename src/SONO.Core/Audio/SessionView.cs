namespace SONO.Core.Audio;

/// <summary>Immutable snapshot of one live audio session, for the UI layer.</summary>
public sealed record SessionView(
    string Key,            // stable per live session instance
    string Exe,            // lower-case executable name ("spotify"); "system" = Windows sounds
    string DisplayName,    // prettiest name we could find
    uint Pid,
    bool IsSystem,
    string State,          // "Active" / "Inactive"
    string? ChannelId,     // channel that owns this exe, null = independent
    float Volume,          // current session volume 0..1
    bool Mute)
{
    public bool Owned => ChannelId is not null;
}
