using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SONO.Core.Diagnostics;

namespace SONO.Core.Audio;

/// <summary>
/// v0.3 routing: captures each assigned APP's audio directly (WASAPI process loopback,
/// process-tree inclusive), applies the owning channel's gain/mute, and mixes everything
/// to the real output. Apps keep playing wherever Windows wants; SONO follows the process.
/// </summary>
public sealed class AppRoutingEngine : IDisposable
{
    private sealed class GainNode : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public GainNode(ISampleProvider src, string channelId) { _src = src; ChannelId = channelId; }
        public string ChannelId { get; }
        public float Volume { get; set; } = 1f;
        public bool Muted { get; set; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(Span<float> buffer)
        {
            int n = _src.Read(buffer);
            if (Muted) { buffer[..n].Clear(); return n; }
            if (Math.Abs(Volume - 1f) > 0.0005f)
                for (int i = 0; i < n; i++) buffer[i] *= Volume;
            return n;
        }
    }

    private sealed class ProcStream
    {
        public required int Pid { get; init; }
        public required string Exe { get; init; }
        public required string ChannelId { get; init; }
        public required WasapiRecorder Recorder { get; init; }
        public required GainNode Gain { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, ProcStream> _streams = new();          // pid → stream
    private readonly Dictionary<string, List<GainNode>> _channelGains = new(); // channelId → live gains
    private MixingSampleProvider? _mixer;
    private WasapiPlayer? _output;
    private MMDevice? _outputDevice;
    private Func<IReadOnlyList<ChannelDefinition>>? _channelSource;
    private System.Threading.Timer? _pollTimer;
    private System.Threading.Timer? _outputWatch;
    private string? _lastSig;
    private bool _rebuilding;

    public string? Error { get; private set; }
    public string? OutputDeviceName => _outputDevice?.FriendlyName;
    public int ActiveStreams { get { lock (_gate) return _streams.Count; } }
    public bool IsRunning => _output is not null;

    public void SetChannelSource(Func<IReadOnlyList<ChannelDefinition>> source) => _channelSource = source;

    public void Start()
    {
        lock (_gate)
        {
            _pollTimer?.Dispose();
            _pollTimer = new Timer(_ => SafePoll(), null, 300, 1000);
            _outputWatch?.Dispose();
            _outputWatch = new Timer(_ => SafeOutputWatch(), null, 1000, 3000);
        }
        Rebuild(null);
    }

    // ---------- output ----------

    public void Rebuild(string? preferredRealOutputId) => RebuildInternal(preferredRealOutputId, force: true);

    private void RebuildInternal(string? preferredRealOutputId, bool force)
    {
        lock (_gate)
        {
            if (_rebuilding) return;
            _rebuilding = true;
            try
            {
                var channels = _channelSource?.Invoke() ?? Array.Empty<ChannelDefinition>();
                var mapped = channels.Where(c => c.DeviceId is not null).Select(c => c.DeviceId!).ToHashSet();

                var en = new MMDeviceEnumerator();
                string defaultId = "";
                try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; } catch { }
                if (mapped.Contains(defaultId)) defaultId = "";

                var outDevice = TryDevice(en, preferredRealOutputId is string p && !mapped.Contains(p) ? p : null)
                                ?? TryDevice(en, defaultId)
                                ?? en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                                      .FirstOrDefault(d => !mapped.Contains(d.ID));
                if (outDevice is null) { Error = "no output device available"; return; }

                string sig = outDevice.ID;
                if (!force && _output is not null && sig == _lastSig) return;

                TeardownOutputLocked();
                _outputDevice = outDevice;
                _lastSig = sig;

                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
#pragma warning disable CS0618
                _output = new WasapiPlayerBuilder()
                    .WithDevice(outDevice)
                    .WithSharedMode()
                    .WithLatency(60)
                    .Build();
#pragma warning restore CS0618
                _output.Init(new SampleToWaveProvider(_mixer));
                _output.Play();

                Error = null;
                Log.Write($"AppRouting output='{outDevice.FriendlyName}'");

                // restart all process streams into the fresh mixer
                var pids = _streams.Keys.ToList();
                foreach (var pid in pids)
                {
                    var s = _streams[pid];
                    RemoveStreamLocked(pid);
                    StartStreamLocked(pid, s.Exe, s.ChannelId);
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log.Write($"AppRouting rebuild failed: {ex}");
            }
            finally { _rebuilding = false; }
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

    private void TeardownOutputLocked()
    {
        foreach (var k in _channelGains.Keys) _channelGains[k].Clear();
        _mixer = null;
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _outputDevice?.Dispose();
        _outputDevice = null;
    }

    // ---------- process streams ----------

    private void SafePoll()
    {
        try { Poll(); }
        catch (Exception ex) { Log.Write($"AppRouting poll failed: {ex}"); }
    }

    private void Poll()
    {
        var channels = _channelSource?.Invoke() ?? Array.Empty<ChannelDefinition>();
        var exeToChannel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in channels)
            foreach (var exe in ch.Executables)
                exeToChannel[exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe] = ch.Id;
        if (exeToChannel.Count == 0) { lock (_gate) { foreach (var pid in _streams.Keys.ToList()) RemoveStreamLocked(pid); } return; }

        // find pids that actually OWN an audio session on any active render device
        var audioPids = new Dictionary<int, string>();   // pid → exe
        var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var col = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < col.Count; i++)
                {
                    uint pidU;
                    try { pidU = col[i].GetProcessID; } catch { continue; }
                    int pid = unchecked((int)pidU);
                    if (pid == 0 || pidU > int.MaxValue || audioPids.ContainsKey(pid)) continue;
                    string pname;
                    try { using var p = Process.GetProcessById(pid); pname = p.ProcessName; }
                    catch { continue; }
                    if (exeToChannel.TryGetValue(pname, out var channelId))
                        audioPids[pid] = channelId;
                }
            }
            catch { }
        }

        lock (_gate)
        {
            // retire captures that no longer play or changed channel
            foreach (var pid in _streams.Keys.ToList())
                if (!audioPids.TryGetValue(pid, out var ch) || _streams[pid].ChannelId != ch)
                    RemoveStreamLocked(pid);

            // start captures for new audio-owning pids
            foreach (var (pid, channelId) in audioPids)
                if (!_streams.ContainsKey(pid))
                    StartStreamLocked(pid, "", channelId);
        }
    }

    private void StartStreamLocked(int pid, string exe, string channelId)
    {
        try
        {
#pragma warning disable CS0618
            var builder = new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)pid, ProcessLoopbackMode.ExcludeTargetProcessTree)
                .WithSharedMode()
                .WithBufferLength(80);
#pragma warning restore CS0618

            // process-loopback activation is async-only; continue on the threadpool
            var chSnap = _channelSource?.Invoke().FirstOrDefault(c => c.Id == channelId);
            Task.Run(async () =>
            {
                WasapiRecorder recorder;
                try { recorder = await builder.BuildAsync(); }
                catch (Exception ex)
                {
                    Log.Write($"AppRouting capture build failed pid={pid} '{exe}': {ex.Message}");
                    lock (_gate) { _streams.Remove(pid); }
                    return;
                }

                lock (_gate)
                {
                    try
                    {
                        ISampleProvider chain = new BufferFeed(recorder);
                        const int MixerRate = 48000, MixerChans = 2;
                        if (chain.WaveFormat.SampleRate != MixerRate || chain.WaveFormat.Channels != MixerChans)
                            chain = new WdlResamplingSampleProvider(chain, MixerRate);
                        if (chain.WaveFormat.Channels == 1)
                            chain = new MonoToStereoSampleProvider(chain);

                        var gain = new GainNode(chain, channelId);
                        gain.Volume = chSnap?.Muted == true ? 0f : chSnap?.Volume ?? 1f;
                        gain.Muted = chSnap?.Muted == true;

                        if (!_channelGains.TryGetValue(channelId, out var list))
                            _channelGains[channelId] = list = new();
                        list.Add(gain);
                        _mixer?.AddMixerInput(gain);

                        _streams[pid] = new ProcStream { Pid = pid, Exe = exe, ChannelId = channelId, Recorder = recorder, Gain = gain };
                        recorder.StartRecording();
                        Log.Write($"AppRouting + '{exe}' pid={pid} → {channelId[..8]}");
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"AppRouting capture wire failed pid={pid} '{exe}': {ex.Message}");
                        try { recorder.Dispose(); } catch { }
                        _streams.Remove(pid);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Write($"AppRouting capture start failed pid={pid} '{exe}': {ex.Message}");
        }
    }

    private void RemoveStreamLocked(int pid)
    {
        if (!_streams.Remove(pid, out var s)) return;
        try { s.Recorder.StopRecording(); } catch { }
        try { s.Recorder.Dispose(); } catch { }
        if (_channelGains.TryGetValue(s.ChannelId, out var list)) list.Remove(s.Gain);
        try { _mixer?.RemoveMixerInput(s.Gain); } catch { }
        Log.Write($"AppRouting − '{s.Exe}' pid={pid}");
    }

    // ---------- control ----------

    public void ApplyVolumes(IReadOnlyList<ChannelDefinition> channels)
    {
        lock (_gate)
        {
            foreach (var ch in channels)
                if (_channelGains.TryGetValue(ch.Id, out var list))
                    foreach (var g in list)
                    { g.Volume = ch.Muted ? 0f : ch.Volume; g.Muted = ch.Muted; }
        }
    }

    private void SafeOutputWatch()
    {
        try
        {
            // follow the Windows default when we're on it, and recover from device loss
            var en = new MMDeviceEnumerator();
            string currentId = _outputDevice?.ID ?? "";
            bool currentGone = false;
            try { currentGone = en.GetDevice(currentId).State != DeviceState.Active; } catch { currentGone = true; }
            var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            bool weAreDefault = currentId == def.ID;
            bool defChanged = false;
            try
            {
                // we only chase the default if we were following it before
                defChanged = weAreDefault && currentId != def.ID;
            }
            catch { }

            if (_output is null || currentGone || defChanged)
                RebuildInternal(null, force: currentGone || _output is null);
        }
        catch { }
    }

    /// <summary>Bridges WasapiRecorder's push callback into a pull-model sample provider.
    /// Recorder delivers float PCM bytes; we buffer and hand them to the mixer on Read.</summary>
    private sealed class BufferFeed : ISampleProvider
    {
        private readonly WasapiRecorder _rec;
        private readonly object _lock = new();
        private byte[] _buf = new byte[1 << 16];
        private int _read, _write;

        public BufferFeed(WasapiRecorder rec)
        {
            _rec = rec;
            _rec.DataAvailable += OnData;
        }

        public WaveFormat WaveFormat => _rec.WaveFormat;

        private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long pos, long qpc)
        {
            lock (_lock)
            {
                int avail = _write - _read;
                if (avail + data.Length > _buf.Length)
                {
                    if (avail + data.Length > _buf.Length / 2)
                        Array.Resize(ref _buf, Math.Max(_buf.Length * 2, (avail + data.Length) * 2));
                    Array.Copy(_buf, _read, _buf, 0, avail);
                    _read = 0;
                    _write = avail;
                }
                data.CopyTo(_buf.AsSpan(_write));
                _write += data.Length;
            }
        }

        public int Read(Span<float> buffer)
        {
            lock (_lock)
            {
                int bytesAvail = _write - _read;
                int takeBytes = Math.Min(bytesAvail, buffer.Length * 4);
                takeBytes -= takeBytes % 4;   // whole floats only
                if (takeBytes == 0) return 0;
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_buf.AsSpan(_read, takeBytes));
                src.CopyTo(buffer);
                _read += takeBytes;
                // compact occasionally to keep the front from marching forward forever
                if (_read > _buf.Length / 2 && _write - _read < _buf.Length / 4)
                {
                    Array.Copy(_buf, _read, _buf, 0, _write - _read);
                    _write -= _read;
                    _read = 0;
                }
                return src.Length;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pollTimer?.Dispose();
            _outputWatch?.Dispose();
            foreach (var pid in _streams.Keys.ToList()) RemoveStreamLocked(pid);
            TeardownOutputLocked();
        }
    }
}
