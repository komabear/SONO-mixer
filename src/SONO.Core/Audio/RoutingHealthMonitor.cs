using System.Diagnostics;
using NAudio.CoreAudioApi;
using SONO.Core.Diagnostics;

namespace SONO.Core.Audio;

/// <summary>
/// Watches where assigned apps are actually playing. A channel's apps are "healthy" when
/// every live session of their exes renders on the channel's own device; anything else is
/// reported as mis-routed so the UI can guide the user (Windows has no API to set this
/// programmatically — per-app outputs are user-facing settings only).
/// </summary>
public sealed class RoutingHealthMonitor
{
    private readonly object _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public sealed class MisRoute
    {
        public required string Exe { get; init; }
        public required string ChannelName { get; init; }
        public required string ActualDevice { get; init; }
    }

    /// <summary>Scan all render devices; report assigned apps not playing on their channel's device.</summary>
    public List<MisRoute> Scan(IReadOnlyList<ChannelDefinition> channels)
    {
        var result = new List<MisRoute>();
        try
        {
            var en = new MMDeviceEnumerator();
            var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var channelByDevice = channels.Where(c => c.DeviceId is not null).ToDictionary(c => c.DeviceId!, c => c);

            foreach (var dev in devices)
            {
                AudioSessionManager? mgr = null;
                try { mgr = dev.AudioSessionManager; } catch { continue; }
                if (mgr is null) continue;
                var col = mgr.Sessions;
                for (int i = 0; i < col.Count; i++)
                {
                    uint pidU;
                    try { pidU = col[i].GetProcessID; } catch { continue; }
                    if (pidU == 0) continue;
                    string pname;
                    try { using var p = Process.GetProcessById(unchecked((int)pidU)); pname = p.ProcessName; }
                    catch { continue; }

                    foreach (var ch in channels)
                    {
                        if (!ch.Executables.Contains(pname, StringComparer.OrdinalIgnoreCase)) continue;
                        if (ch.DeviceId is null) continue;
                        if (!string.Equals(dev.ID, ch.DeviceId, StringComparison.OrdinalIgnoreCase)
                            && !dev.FriendlyName.Contains("SONO - " + ch.Name, StringComparison.OrdinalIgnoreCase))
                            result.Add(new MisRoute { Exe = pname, ChannelName = ch.Name, ActualDevice = dev.FriendlyName });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"RoutingHealth scan failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>True the first time a given exe is seen mis-routed (for one-time notifications).</summary>
    public bool ShouldNotify(MisRoute m)
    {
        lock (_gate) return _reported.Add(m.Exe + "|" + m.ChannelName);
    }
}
