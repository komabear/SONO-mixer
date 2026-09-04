namespace SONO.Core.Audio;

public sealed record MicView(
    bool Available,
    string DeviceName,
    float Volume,   // channel-controlled volume; -1 when not touched
    bool Muted);

public sealed record EngineSnapshot(
    IReadOnlyList<SessionView> Sessions,
    MicView? Mic,
    string OutputDevice,
    string? Error);
