using System.Diagnostics;
using System.Globalization;

namespace SONO.Core.Audio;

/// <summary>
/// Owns WASAPI: enumerates render sessions each tick and applies channel ownership
/// Sonar-style — a claimed app's session volume IS its channel's volume. Anything
/// the engine touched gets restored to 100%/unmuted when ownership goes away, and
/// on shutdown (final pass with an empty channel list).
///
/// Session enumeration is NATIVE (NativeSessionEnumerator): every COM pointer is
/// released deterministically inside the tick, so the Windows Audio service no longer
/// accumulates server-side state. This replaces the old NAudio path whose
/// RefreshSessions allocated an RCW-finalized enumerator per call (~150MB/hour in
/// Audiosrv at a 600ms poll — the reason session refresh had to be rate-limited to
/// 1×/5s). With the leak gone, every tick enumerates fresh again: new apps appear
/// within one tick instead of up to 5s.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (float Vol, bool Mute)> _touched = new();
    private readonly Dictionary<string, (float? Vol, bool? Mute)> _independent = new();
    private readonly Dictionary<uint, (string Exe, string Title)> _pidCache = new();
    private readonly Dictionary<uint, string> _pidPathCache = new();
    private Func<IReadOnlyList<ChannelDefinition>>? _channelSource;
    private System.Threading.Timer? _timer;

    public event Action<EngineSnapshot>? Tick;

    /// <summary>Engine pulls the current channel list every tick; call whenever settings change.</summary>
    public void SetChannelSource(Func<IReadOnlyList<ChannelDefinition>> source) => _channelSource = source;

    public void Start(int periodMs = 600)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => SafeReconcile(), null, 0, periodMs);
    }

    /// <summary>Immediate reconcile outside the poll (e.g. right after a slider move).
    /// Runs on the threadpool: the full session enumeration is COM/pid lookups — never
    /// on the UI thread (that was the slider lag). Tick marshals to UI.</summary>
    public void ReconcileNow() => Task.Run(SafeReconcile);

    /// <summary>Refresh the session list now (window focus). Kept for API compatibility:
    /// native enumeration is fresh EVERY tick, so this is just an immediate reconcile.</summary>
    public void RefreshSessionsNow() => ReconcileNow();

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
            var sessions = new List<SessionView>();
            string outputName = "?";
            try
            {
                // exe → owning channel (first channel wins if the same exe was added twice)
                var byExe = channels.Where(c => c.Kind == ChannelKind.Group)
                    .SelectMany(c => c.Executables.Select(e => (exe: e.ToLowerInvariant(), ch: c)))
                    .GroupBy(x => x.exe)
                    .ToDictionary(g => g.Key, g => g.First().ch);

                // sessions of ALL active render endpoints, fresh every tick, all COM
                // pointers released deterministically inside the enumerator
                var natives = NativeSessionEnumerator.EnumerateAllRenderSessions(out _, out outputName, out string? error);

                var seen = new HashSet<string>();
                foreach (var nat in natives)
                {
                    try
                    {
                        if (seen.Add(nat.Key))   // dedupe sessions visible on several endpoints
                        {
                            var view = CollectSession(in nat, byExe);
                            if (view is not null) sessions.Add(view);
                        }
                    }
                    finally { NativeSessionEnumerator.ReleaseSession(in nat); }
                }
                if (error is not null) SONO.Core.Diagnostics.Log.Write($"enumerate failed: {error}");

                // forget touched entries whose session died (nothing left to restore)
                if (_touched.Count > 0)
                    foreach (var dead in _touched.Keys.Where(k => !seen.Contains(k)).ToList())
                        _touched.Remove(dead);

                return new EngineSnapshot(sessions, outputName, error);
            }
            catch (Exception ex)
            {
                return new EngineSnapshot(sessions, outputName, ex.Message);
            }
        }
    }

    /// <summary>Apply ownership/volume for one native session and build its UI view.
    /// Returns null only for expired sessions. COM pointers stay owned by the caller.</summary>
    private SessionView? CollectSession(in NativeSessionEnumerator.NativeSession nat, Dictionary<string, ChannelDefinition> byExe)
    {
        if (nat.State == 2) return null;   // AudioSessionState.Expired

        var (exe, title) = Identify(nat.Pid);
        if (exe.Length == 0) exe = "unknown";
        string key = nat.Key;

        string? channelId = null;
        ChannelDefinition? owner = byExe.TryGetValue(exe, out var found) ? found : null;
        if (owner is not null) channelId = owner.Id;
        SONO.Core.Diagnostics.Log.Write($"attrib: pid={nat.Pid} exe='{exe}' path='{PathOf(nat.Pid)}' → {(owner is not null ? owner.Name : "(none)")}");

        if (nat.VolumePtr != 0)
        {
            if (owner is not null)
            {
                var target = (Vol: owner.Volume, Mute: owner.Muted);
                try
                {
                    if (NativeSessionEnumerator.VolumeGet(nat.VolumePtr, out float cur) &&
                        Math.Abs(cur - target.Vol) > 0.001f)
                        NativeSessionEnumerator.VolumeSet(nat.VolumePtr, target.Vol);
                    if (NativeSessionEnumerator.VolumeGetMute(nat.VolumePtr, out bool curM) &&
                        curM != target.Mute)
                        NativeSessionEnumerator.VolumeSetMute(nat.VolumePtr, target.Mute);
                }
                catch { }
                _touched[key] = target;
            }
            else
            {
                if (_touched.Remove(key))
                {
                    // ownership lost but session still alive → give the app its independence back
                    try
                    {
                        NativeSessionEnumerator.VolumeSet(nat.VolumePtr, 1f);
                        NativeSessionEnumerator.VolumeSetMute(nat.VolumePtr, false);
                    }
                    catch { }
                }
                if (_independent.TryGetValue(exe, out var ind))
                {
                    try
                    {
                        bool getV = NativeSessionEnumerator.VolumeGet(nat.VolumePtr, out float curV);
                        bool getM = NativeSessionEnumerator.VolumeGetMute(nat.VolumePtr, out bool curM);
                        if (getV && ind.Vol is float iv && Math.Abs(curV - iv) > 0.001f)
                            NativeSessionEnumerator.VolumeSet(nat.VolumePtr, iv);
                        if (getM && ind.Mute is bool im && curM != im)
                            NativeSessionEnumerator.VolumeSetMute(nat.VolumePtr, im);
                    }
                    catch { }
                }
            }
        }
        else if (owner is not null)
        {
            _touched[key] = (owner.Volume, owner.Muted);   // remember intent even if not enforceable
        }

        float curVol = 1f; bool curMute = false;
        try
        {
            if (nat.VolumePtr != 0)
            {
                NativeSessionEnumerator.VolumeGet(nat.VolumePtr, out curVol);
                NativeSessionEnumerator.VolumeGetMute(nat.VolumePtr, out curMute);
            }
        }
        catch { }

        string stateStr = nat.State switch { 1 => "Active", 2 => "Expired", _ => "Inactive" };
        return new SessionView(
            key, exe, PrettyName(in nat, nat.Pid, exe, title), nat.Pid, nat.Pid == 0,
            stateStr, channelId, curVol, curMute);
    }

    /// <summary>Full executable path for a pid ('' if unavailable).</summary>
    private string PathOf(uint pid)
    {
        if (_pidPathCache.TryGetValue(pid, out var pth)) return pth;
        string path = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            path = p.MainModule?.FileName?.ToLowerInvariant() ?? "";
        }
        catch { }
        if (_pidPathCache.Count > 256) _pidPathCache.Clear();
        _pidPathCache[pid] = path;
        return path;
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

    private string PrettyName(in NativeSessionEnumerator.NativeSession nat, uint pid, string exe, string title)
    {
        if (pid == 0) return "System Sounds";
        try { if (!string.IsNullOrWhiteSpace(nat.DisplayName)) return nat.DisplayName; } catch { }
        if (!string.IsNullOrWhiteSpace(title)) return title;
        // generic Chromium renderer names: brand from the install folder
        // (root\current\chromium\... shows as "Root"; youtube-music\... as "Youtube Music")
        if (exe is "chromium" or "chrome" or "cef")
        {
            var path = PathOf(pid);
            foreach (var brand in BrandFromPath(path))
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(brand);
        }
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(exe);
    }

    /// <summary>Extract a human app name from a Chromium binary's install path —
    /// Electron layout: install\app-version\name.exe or install\name\chromium\... —
    /// the folder above the exe is the brand.</summary>
    private static IEnumerable<string> BrandFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // walk from the exe's folder upward, skip known generic folder names
        for (int i = parts.Length - 2; i >= 0 && i >= parts.Length - 4; i--)
        {
            var folder = parts[i].ToLowerInvariant();
            if (folder is "chromium" or "windowsx64" or "current" or "app" or "bin")
                continue;
            yield return parts[i];
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            try { Reconcile(Array.Empty<ChannelDefinition>()); } catch { } // restore everything we touched
        }
    }
}
