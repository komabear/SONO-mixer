namespace SONO.Core.Audio;

public sealed record EngineSnapshot(
    IReadOnlyList<SessionView> Sessions,
    string OutputDevice,
    string? Error);
