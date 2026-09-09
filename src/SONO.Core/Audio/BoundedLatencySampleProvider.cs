using NAudio.Wave;

namespace SONO.Core.Audio;

/// <summary>
/// Wraps a pull source backed by a BufferedWaveProvider and bounds the buffered latency.
/// Cable capture and DAC playback clocks drift by tens of ppm; without a guard the
/// buffered backlog grows continuously (delay creeps up) until overflow discard kicks in.
///
/// Drift correction: when buffered audio exceeds <paramref name="max"/>, the OLDEST
/// excess is trimmed by READING AND DISCARDING exactly the surplus bytes — a partial
/// splice of (buffered − target), not a full ClearBuffer (NAudio 3.x's only clear API
/// drops the whole backlog, which was an audible 100–150 ms hole: the Discord pops).
/// </summary>
public sealed class BoundedLatencySampleProvider : ISampleProvider
{
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _source;
    private readonly long _targetBytes;
    private readonly long _maxBytes;
    private readonly float[] _discard;
    private long _nextTrimCheck;

    public BoundedLatencySampleProvider(BufferedWaveProvider buffer, ISampleProvider source,
        TimeSpan target, TimeSpan max)
    {
        _buffer = buffer;
        _source = source;
        int align = source.WaveFormat.BlockAlign;
        _targetBytes = (long)(source.WaveFormat.AverageBytesPerSecond * target.TotalSeconds) / align * align;
        _maxBytes = (long)(source.WaveFormat.AverageBytesPerSecond * max.TotalSeconds) / align * align;
        // scratch for reading-and-discarding; grows to the worst-case trim on first use
        _discard = new float[(int)(_maxBytes / sizeof(float)) + source.WaveFormat.Channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // diagnostics (logged periodically by Poll in RoutingEngine)
    internal long TrimCount;
    internal long UnderrunCount;

    public int Read(Span<float> buffer)
    {
        // bounded drift trim (checked at most every ~250 ms — cheap)
        if (Environment.TickCount64 - _nextTrimCheck > 250)
        {
            _nextTrimCheck = Environment.TickCount64;
            long buffered = _buffer.BufferedBytes;
            if (buffered > _maxBytes)
            {
                long excess = buffered - _targetBytes;
                TrimBytes((int)excess);
                TrimCount++;
            }
            else if (buffered < _source.WaveFormat.AverageBytesPerSecond / 20)   // < 50 ms
            {
                UnderrunCount++;   // consuming faster than delivering: dry-buffer risk
            }
        }
        return _source.Read(buffer);
    }

    /// <summary>Read-and-discard exactly <paramref name="bytes"/> from the source,
    /// in chunks through the scratch buffer. Sample-aligned by construction: the provider
    /// consumes whole frames, so discarding N frames removes exactly N×BlockAlign bytes.</summary>
    private void TrimBytes(int bytes)
    {
        int frame = _source.WaveFormat.Channels * sizeof(float);   // bytes per float frame
        int frames = bytes / frame;
        while (frames > 0)
        {
            int take = Math.Min(frames, _discard.Length / _source.WaveFormat.Channels);
            int got = _source.Read(_discard.AsSpan(0, take * _source.WaveFormat.Channels));
            if (got <= 0) break;   // buffer ran dry unexpectedly — nothing left to trim
            frames -= got / _source.WaveFormat.Channels;
        }
    }
}
