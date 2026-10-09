using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;

using System.Runtime.InteropServices;

namespace SONO.Core.Audio;

/// <summary>
/// Owns WASAPI: enumerates render sessions each tick and applies channel ownership
/// Sonar-style — a claimed app's session volume IS its channel's volume. Anything
/// the engine touched gets restored to 100%/unmuted when ownership goes away, and
/// on shutdown (final pass with an empty channel list).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (float Vol, bool Mute)> _touched = new();
    private readonly Dictionary<string, (float? Vol, bool? Mute)> _independent = new();
    private readonly Dictionary<uint, (string Exe, string Title)> _pidCache = new();
    private Func<IReadOnlyList<ChannelDefinition>>? _channelSource;
    private System.Threading.Timer? _timer;
    private MMDevice? _render;
    private List<MMDevice>? _tempDevices;
    private readonly Dictionary<string, MMDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioSessionManager> _sessionManagers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SimpleAudioVolume> _volumeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>dev.AudioSessionManager allocates a fresh COM wrapper per call; cache per
    /// device so ticks reuse one instance (native COM leak fix).</summary>
    private AudioSessionManager GetSessionManager(MMDevice dev)
    {
        if (_sessionManagers.TryGetValue(dev.ID, out var mgr)) return mgr;
        mgr = dev.AudioSessionManager;
        _sessionManagers[dev.ID] = mgr;
        return mgr;
    }
    public event Action<EngineSnapshot>? Tick;

    /// <summary>Engine pulls the current channel list every tick; call whenever settings change.</summary>
    public void SetChannelSource(Func<IReadOnlyList<ChannelDefinition>> source) => _channelSource = source;

    private System.Threading.Timer? _meterTimer;
    /// <summary>Fast peak readout (~60ms): channelKey → peak 0..1. Cheap: meter reads on
    /// cached sessions only, no full reconcile — smooth VU motion.</summary>
    public event Action<Dictionary<string, float>>? LevelsTick;

    public void Start(int periodMs = 600)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => SafeReconcile(), null, 0, periodMs);
        _meterTimer?.Dispose();
        _meterTimer = new System.Threading.Timer(_ => SafeLevels(), null, 25, 25);
    }

    /// <summary>Immediate reconcile outside the poll (e.g. right after a slider move).
    /// Runs on the threadpool: the full session enumeration is 50–200 ms of COM/pid
    /// lookups — never on the UI thread (that was the slider lag). Tick marshals to UI.</summary>
    public void ReconcileNow() => Task.Run(SafeReconcile);

    /// <summary>Per-app volume/mute for apps NOT in any channel (right-panel sliders). exe lower-case.</summary>
    public void SetIndependentVolume(string exe, float? vol, bool? mute)
    {
        lock (_gate) { _independent[exe.ToLowerInvariant()] = (vol, mute); }
        ReconcileNow();
    }

    private void SafeReconcile()
    {
        EngineSnapshot snap;
        try { snap = Reconcile(_channelSource?.Invoke() ?? Array.Empty<ChannelDefinition>()); }
        catch (Exception ex) { snap = new EngineSnapshot(Array.Empty<SessionView>(), "?", ex.Message); }
        try { Tick?.Invoke(snap); }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"tick handler failed: {ex}"); } // a UI bug must never kill audio
    }

    private EngineSnapshot Reconcile(IReadOnlyList<ChannelDefinition> channels)
    {
        lock (_gate)
        {
            string? error = null;
            var sessions = new List<SessionView>();
            string outputName = "?";

            try
            {
                // re-resolve the default device when it changes (audiosesrv switches can
                // invalidate the cached MMDevice, making the session view go stale)
                var currentDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                if (_render is null || _render.ID != currentDefault.ID)
                {
                    _render?.Dispose();
                    _render = currentDefault;
                }
                outputName = _render.FriendlyName;

                // exe → owning channel (first channel wins if the same exe was added twice)
                var byExe = channels.Where(c => c.Kind == ChannelKind.Group)
                    .SelectMany(c => c.Executables.Select(e => (exe: e.ToLowerInvariant(), ch: c)))
                    .GroupBy(x => x.exe)
                    .ToDictionary(g => g.Key, g => g.First().ch);

                // enumerate sessions on ALL active render devices (an app mid-misroute, or on
                // a cable, is exactly the app the user needs to see in the Applications list)
                var seen = new HashSet<string>();
                _tempDevices = new List<MMDevice>();
                var fresh = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                var activeIds = new HashSet<string>(fresh.Select(d => d.ID));
                // keep ONE long-lived MMDevice per endpoint: disposing an MMDevice also disposes its
                // AudioSessionManager, so the cached manager must outlive the per-tick wrappers
                foreach (var d in fresh)
                {
                    if (_devices.ContainsKey(d.ID)) _tempDevices.Add(d);   // duplicate wrapper → dispose in finally
                    else _devices[d.ID] = d;
                }
                foreach (var gone in _devices.Keys.Where(k => !activeIds.Contains(k)).ToList())
                {
                    try { _devices[gone].Dispose(); } catch { }
                    _devices.Remove(gone);
                    _sessionManagers.Remove(gone);
                }
                foreach (var dev in activeIds.Select(id => _devices[id]))
                {
                    AudioSessionManager? mgr;
                    try { mgr = GetSessionManager(dev); } catch { continue; }
                    // NAudio caches the session list; refresh so apps that start playing later appear
                    try { mgr.RefreshSessions(); } catch (Exception rex) { SONO.Core.Diagnostics.Log.Write($"RefreshSessions failed on {dev.FriendlyName}: {rex.Message}"); continue; }
                    var sessions2 = mgr.Sessions;
                    for (int i = 0; i < sessions2.Count; i++)
                    {
                        try
                        {
                            CollectSession(sessions2[i], dev.FriendlyName, byExe, sessions, seen);
                        }
                        catch { }
                    }
                }

                // forget touched entries whose session died (nothing left to restore)
                if (_touched.Count > 0)
                    foreach (var dead in _touched.Keys.Where(k => !seen.Contains(k)).ToList())
                        _touched.Remove(dead);
                foreach (var dead in _volumeCache.Keys.Where(k => !seen.Contains(k)).ToList())
                    _volumeCache.Remove(dead);

                return new EngineSnapshot(sessions, outputName, null);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _render = null;   // device may have changed; retry from scratch next tick
                return new EngineSnapshot(sessions, outputName, error);
            }
            finally
            {
                // MMDevice COM objects from EnumerateAudioEndPoints MUST be disposed every tick
                // or they accumulate as native memory (~15MB/min observed).
                // NOTE: AudioSessionManagers obtained from them are CACHED (see GetSessionManager)
                // and must NOT be released here — they stay alive for the device's lifetime.
                if (_tempDevices is not null)
                {
                    foreach (var d in _tempDevices) { try { d.Dispose(); } catch { } }
                    _tempDevices = null;
                }
            }
        }
    }

    private void CollectSession(
        AudioSessionControl asc, string deviceName,
        Dictionary<string, ChannelDefinition> byExe,
        List<SessionView> sessions, HashSet<string> seen)
    {
        string stateStr;
        uint pid;
        string key;
        try
        {
            stateStr = asc.State.ToString();
            if (stateStr.Contains("Expired")) return;
            pid = asc.GetProcessID;
            key = SessionKey(pid, asc);
        }
        catch { return; } // session died between enumeration and read

        if (!seen.Add(key)) return;   // already listed from another device
        var (exe, title) = Identify(pid);
        if (exe.Length == 0) exe = "unknown";

        string? channelId = null;
        ChannelDefinition? owner = byExe.TryGetValue(exe, out var found) ? found : null;
        if (owner is not null) channelId = owner.Id;

        if (owner is not null)
        {
            var target = (Vol: owner.Volume, Mute: owner.Muted);
            try
            {
                // asc.SimpleAudioVolume allocates a fresh COM wrapper per access — cache per
                // session so idle ticks don't leak native ISimpleAudioVolume/AudioMixer objects
                if (!_volumeCache.TryGetValue(key, out var vol))
                {
                    vol = asc.SimpleAudioVolume;
                    _volumeCache[key] = vol;
                }
                if (Math.Abs(vol.Volume - target.Vol) > 0.001f) vol.Volume = target.Vol;
                if (vol.Mute != target.Mute) vol.Mute = target.Mute;
            }
            catch { }
            _touched[key] = target;
        }
        else
        {
            if (_touched.Remove(key))
            {
                // ownership lost but session still alive → give the app its independence back
                try { asc.SimpleAudioVolume.Volume = 1f; asc.SimpleAudioVolume.Mute = false; } catch { }
            }
            if (_independent.TryGetValue(exe, out var ind))
            {
                try
                {
                    if (ind.Vol is float iv && Math.Abs(asc.SimpleAudioVolume.Volume - iv) > 0.001f)
                        asc.SimpleAudioVolume.Volume = iv;
                    if (ind.Mute is bool im && asc.SimpleAudioVolume.Mute != im)
                        asc.SimpleAudioVolume.Mute = im;
                }
                catch { }
            }
        }

        float curVol = 1f; bool curMute = false;
        try { curVol = asc.SimpleAudioVolume.Volume; curMute = asc.SimpleAudioVolume.Mute; } catch { }
        float peak = 0f;
        try { peak = asc.AudioMeterInformation.MasterPeakValue; } catch { }
        _meterSessions[key] = (asc, channelId);

        sessions.Add(new SessionView(
            key, exe, PrettyName(asc, pid, exe, title), pid, pid == 0,
            stateStr.Replace("AudioSessionState", ""), channelId, curVol, curMute, peak));
    }

    private static string SessionKey(uint pid, AudioSessionControl asc)
        => $"{pid}:{asc.GetSessionIdentifier?.GetHashCode()}";

    private readonly Dictionary<string, (AudioSessionControl asc, string? channelId)> _meterSessions = new();

    private void SafeLevels()
    {
        try
        {
            var result = new Dictionary<string, float>();
            lock (_gate)
            {
                foreach (var (key, (asc, channelId)) in _meterSessions)
                {
                    if (channelId is null) continue;
                    float peak = 0f;
                    try { peak = asc.AudioMeterInformation.MasterPeakValue; } catch { }
                    if (peak > 0f) result[channelId] = Math.Max(peak, result.GetValueOrDefault(channelId));
                }
            }
            if (result.Count > 0) LevelsTick?.Invoke(result);
        }
        catch { }
    }

    private (string Exe, string Title) Identify(uint pid)
    {
        if (pid == 0) return ("system", "");
        if (_pidCache.TryGetValue(pid, out var hit)) return hit;
        string exe = "", title = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            exe = p.ProcessName.ToLowerInvariant();
            // MainWindowTitle is an expensive Win32 enumeration; only try it ONCE per pid and
            // accept "no window" without retrying (firewall: a stubborn pid must not cost us
            // a full window walk every tick)
            try { title = p.MainWindowTitle ?? ""; } catch { title = ""; }
        }
        catch { /* process exited */ }
        var v = (exe, title);
        if (exe.Length > 0)
        {
            if (_pidCache.Count > 256) _pidCache.Clear();
            _pidCache[pid] = v;
        }
        return v;
    }

    private static string PrettyName(AudioSessionControl asc, uint pid, string exe, string title)
    {
        if (pid == 0) return "System Sounds";
        try { if (!string.IsNullOrWhiteSpace(asc.DisplayName)) return asc.DisplayName; } catch { }
        if (!string.IsNullOrWhiteSpace(title)) return title;
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(exe);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            try { Reconcile(Array.Empty<ChannelDefinition>()); } catch { } // restore everything we touched
            _render?.Dispose();
            _render = null;
            foreach (var m in _sessionManagers.Values) { try { Marshal.ReleaseComObject(m); } catch { } }
            _sessionManagers.Clear();
            foreach (var d in _devices.Values) { try { d.Dispose(); } catch { } }
            _devices.Clear();
            _volumeCache.Clear();
        }
    }
}
