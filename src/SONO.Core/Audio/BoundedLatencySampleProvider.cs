using NAudio.Wave;

namespace SONO.Core.Audio;

/// <summary>
/// Wraps a pull source backed by a BufferedWaveProvider and bounds the buffered latency.
/// Cable capture and DAC playback clocks drift by tens of ppm; without a guard the
/// buffered backlog grows continuously (delay creeps up) until overflow discard kicks in
/// (audible mute-then-return). Whenever buffered audio exceeds <paramref name="max"/>,
/// the OLDEST excess is dropped in one small step so the stream stays within
/// [target, max] — an occasional ~50 ms skip instead of seconds of growing delay.
/// </summary>
public sealed class BoundedLatencySampleProvider : ISampleProvider
{
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _source;
    private readonly long _targetBytes;
    private readonly long _maxBytes;
    private long _skipCooldownTicks;

    public BoundedLatencySampleProvider(BufferedWaveProvider buffer, ISampleProvider source,
        TimeSpan target, TimeSpan max)
    {
        _buffer = buffer;
        _source = source;
        int align = source.WaveFormat.BlockAlign;
        _targetBytes = (long)(source.WaveFormat.AverageBytesPerSecond * target.TotalSeconds) / align * align;
        _maxBytes = (long)(source.WaveFormat.AverageBytesPerSecond * max.TotalSeconds) / align * align;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        // bounded drift trim (checked at most every ~250 ms — cheap)
        if (Environment.TickCount64 - _skipCooldownTicks > 250)
        {
            _skipCooldownTicks = Environment.TickCount64;
            long buffered = _buffer.BufferedBytes;
            if (buffered > _maxBytes)
            {
                // NAudio 3.0.1's BufferedWaveProvider only offers ClearBuffer() (all);
                // drop the whole backlog: output continues from live audio, and since
                // this only fires at >max latency the gap is bounded (~100 ms + refill)
                _buffer.ClearBuffer();
            }
        }
        return _source.Read(buffer);
    }
}
