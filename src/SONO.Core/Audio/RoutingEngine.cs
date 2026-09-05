using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SONO.Core.Audio;

/// <summary>
/// True Sonar-style routing: for every channel mapped to a playback device (a VAC Line),
/// captures that device's loopback, applies channel gain/mute, mixes everything, and
/// plays the combined mix to the real output device. Apps set their output to a Line;
/// SONO carries it the rest of the way.
/// </summary>
public sealed class RoutingEngine : IDisposable
{
    public sealed class ChannelStream
    {
        public required string ChannelId { get; init; }
        public required string DeviceId { get; init; }
#pragma warning disable CS0618
        public required WasapiLoopbackCapture Capture { get; init; }
#pragma warning restore CS0618
        public required BufferedWaveProvider Buffer { get; init; }
        public required VolumeSampleProvider Gain { get; init; }
        public MMDevice? Device { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ChannelStream> _streams = new(); // by channelId
    private readonly Dictionary<string, string> _deviceToChannel = new(); // deviceId → channelId
    private MixingSampleProvider? _mixer;
    private IWavePlayer? _output;
    private MMDevice? _outputDevice;
    private Func<IReadOnlyList<ChannelDefinition>>? _channelSource;
    private string? _realOutputId;
    private bool _running;

    public string? RealOutputId { get => Volatile.Read(ref _realOutputId); private set => _realOutputId = value; }
    public bool IsRunning => _running;
    public int ActiveStreams { get { lock (_gate) return _streams.Count; } }
    public string? Error { get; private set; }

    public void SetChannelSource(Func<IReadOnlyList<ChannelDefinition>> source) => _channelSource = source;

    /// <summary>(Re)build all streams. Called on start, mapping changes, and output-device loss.</summary>
    public void Rebuild(string? preferredRealOutputId = null)
    {
        lock (_gate)
        {
            try
            {
                TeardownLocked();

                var channels = _channelSource?.Invoke() ?? Array.Empty<ChannelDefinition>();
                var mapped = channels.Where(c => c.DeviceId is not null).ToList();
                if (mapped.Count == 0) { _running = false; Error = null; return; }

                var en = new MMDeviceEnumerator();

                // real output: explicit choice → current default → best non-cable device
                var mappedIds = mapped.Select(c => c.DeviceId).ToHashSet();
                var outDevice = TryDevice(en, preferredRealOutputId)
                                ?? TryDevice(en, en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID)
                                ?? en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                                      .FirstOrDefault(d => !mappedIds.Contains(d.ID));
                if (outDevice is null) { Error = "no output device available"; return; }
                _outputDevice = outDevice;
                RealOutputId = outDevice.ID;

                const int MixerRate = 48000, MixerChans = 2;
                var mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixerRate, MixerChans);
                _mixer = new MixingSampleProvider(mixerFormat) { ReadFully = true };

                foreach (var ch in mapped)
                {
                    var devId = ch.DeviceId ?? "";
                    if (devId.Length == 0) continue;
                    var dev = TryDevice(en, devId);
                    if (dev is null) continue;

#pragma warning disable CS0618 // NAudio 3 deprecates these in favor of the builder API; revisit later
                    var capture = new WasapiLoopbackCapture(dev);
                    var buffer = new BufferedWaveProvider(capture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                    };
                    capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                    ISampleProvider chain = buffer.ToSampleProvider();
                    if (chain.WaveFormat.SampleRate != MixerRate || chain.WaveFormat.Channels != MixerChans)
                        chain = new WdlResamplingSampleProvider(chain, MixerRate);
                    if (chain.WaveFormat.Channels == 1)
                        chain = new MonoToStereoSampleProvider(chain);

                    var gain = new VolumeSampleProvider(chain);
                    gain.Volume = ch.Muted ? 0f : ch.Volume;
                    _mixer.AddMixerInput(gain);

                    _streams[ch.Id] = new ChannelStream
                    {
                        ChannelId = ch.Id,
                        DeviceId = devId,
                        Capture = capture,
                        Buffer = buffer,
                        Gain = gain,
                        Device = dev,
                    };
                    _deviceToChannel[devId] = ch.Id;
                }

                _output = new WasapiOut(outDevice, AudioClientShareMode.Shared, false, 60);
#pragma warning restore CS0618
                _output.Init(new SampleToWaveProvider(_mixer));
                _output.Play();

                foreach (var s in _streams.Values) s.Capture.StartRecording();
                _running = true;
                Error = null;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                TeardownLocked();
            }
        }
    }

    private static MMDevice? TryDevice(MMDeviceEnumerator en, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var d = en.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch { return null; }
    }

    /// <summary>Apply channel volume/mute to the live stream without rebuilding.</summary>
    public void ApplyVolumes(IReadOnlyList<ChannelDefinition> channels)
    {
        lock (_gate)
        {
            foreach (var ch in channels)
                if (_streams.TryGetValue(ch.Id, out var s))
                    s.Gain.Volume = ch.Muted ? 0f : ch.Volume;
        }
    }

    private void TeardownLocked()
    {
        foreach (var s in _streams.Values)
        {
            try { s.Capture.StopRecording(); } catch { }
            try { s.Capture.Dispose(); } catch { }
            s.Device?.Dispose();
        }
        _streams.Clear();
        _deviceToChannel.Clear();
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _mixer = null;
        _outputDevice?.Dispose();
        _outputDevice = null;
        _running = false;
    }

    public void Dispose() { lock (_gate) TeardownLocked(); }
}
