using System.Collections.ObjectModel;
using NAudio.CoreAudioApi;
using SONO.Core.Audio;
using SONO.Core.Settings;

namespace SONO.App.ViewModels;

public sealed record OutputVm(string Id, string Name);

public static class GroupColors
{
    /// <summary>Theme group color (Game/Chat/Media/Aux = 0-3) — the SINGLE source of truth:
    /// card outline/slider/dot, app-row tint/bar, drag ghost, and OSD all read this.</summary>
    public static string Hex(int channelIndex, string fallback = "#7aa2f7")
    {
        var gb = Themes.ThemeManager.Current.GroupBgs;
        return channelIndex >= 0 && channelIndex < gb.Length ? gb[channelIndex] : fallback;
    }

    public static Avalonia.Media.Color Color(int channelIndex) =>
        Avalonia.Media.Color.TryParse(Hex(channelIndex), out var c) ? c : Avalonia.Media.Color.Parse("#7aa2f7");
}

/// <summary>Root VM: owns the engine wiring, settings, hotkeys, and all UI state.</summary>
public sealed class MixerVm : ObservableObject
{
    private readonly AudioEngine _engine;
    private readonly HotkeyManager _hotkeys;
    public AppSettings Settings { get; }

    public ObservableCollection<ChannelVm> Channels { get; } = new();
    public ObservableCollection<AppRowVm> Apps { get; } = new();
    public ObservableCollection<OutputVm> Outputs { get; } = new();

    private string _statusText = "Starting…";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private OutputVm? _selectedOutput;
    public OutputVm? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!Set(ref _selectedOutput, value)) return;
            if (value is null) return;
            lock (Settings) Settings.RealOutputId = value.Id;
            Save();
            SwitchOutput(value);
        }
    }

    /// <summary>Fired when an OSD-worthy volume change happens (channel, volume 0..1).</summary>
    public event Action<string, double>? OsdVolume;
    /// <summary>Fired when an OSD-worthy mute toggle happens (channel, muted).</summary>
    public event Action<string, bool>? OsdMute;

    public MixerVm(AudioEngine engine, HotkeyManager hotkeys, AppSettings settings)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        Settings = settings;

        lock (settings)
            foreach (var def in settings.Channels)
                Channels.Add(new ChannelVm(def, this));

        _engine.SetChannelSource(() =>
        {
            lock (Settings) return Settings.Channels.ToList();
        });
        _engine.Tick += OnTick;
        _hotkeys.Pressed += OnHotkey;
    }

    public void Start() => _engine.Start();

    // ---------------- engine callbacks (threadpool thread!) ----------------

    private void OnTick(EngineSnapshot snap)
    {
        StatusText = snap.Error is not null ? $"⚠ {snap.Error}" : $"Output: {snap.OutputDevice}";

        foreach (var ch in Channels)
        {
            float vol; bool muted;
            lock (Settings) { vol = ch.Def.Volume; muted = ch.Def.Muted; }
            ch.UpdateFromEngine(vol, muted);
            ch.SyncApps();
        }
        RefreshApps(snap);
    }

    private void RefreshApps(EngineSnapshot snap)
    {
        // Apps is UI-bound (ObservableCollection) — all mutations must happen on the UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshAppsCore(snap));
    }

    private void RefreshAppsCore(EngineSnapshot snap)
    {
        // live = apps with a session right now; pinned rows (added via the + picker) survive
        var live = snap.Sessions.Where(s => s.Exe.Length > 0 && s.Exe != "unknown").ToList();

        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            var exe = Apps[i].Exe;
            bool isLive = live.Any(s => s.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
            bool isPinned;
            lock (Settings) isPinned = PinnedApps.Contains(exe, StringComparer.OrdinalIgnoreCase);
            bool inGroup;
            lock (Settings) inGroup = Settings.Channels.Any(c => c.Executables.Contains(exe, StringComparer.OrdinalIgnoreCase));
            if (!isLive && !isPinned && !inGroup)
                Apps.RemoveAt(i);
        }

        Dictionary<string, string> names;
        lock (Settings) names = Settings.Channels.ToDictionary(c => c.Id, c => c.Name);
        var seenExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in live)
        {
            if (!seenExes.Add(s.Exe)) continue;   // one row per exe
            var row = Apps.FirstOrDefault(r => r.Exe.Equals(s.Exe, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new AppRowVm(s);
                Apps.Add(row);
            }
            names.TryGetValue(s.ChannelId ?? "", out var gname);
            row.Update(s, s.ChannelId is null ? null : gname);
        }

        // feed KnownApps with everything we ever see live (assign picker source)
        lock (Settings)
        {
            foreach (var exe in live.Select(s => s.Exe).Distinct(StringComparer.OrdinalIgnoreCase))
                if (exe != "system" && !Settings.KnownApps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                    Settings.KnownApps.Add(exe);
        }

        SortApps();
    }

    /// <summary>Grouped apps first (by channel order), then ungrouped — alphabetical inside each tier.
    /// Runs only when the ordering actually changes, so rows don't reshuffle under the cursor.</summary>
    private void SortApps()
    {
        List<AppRowVm> sorted;
        lock (Settings)
        {
            var order = Channels.Select(c => c.Id).ToList();
            sorted = Apps
                .OrderByDescending(r => r.Group is not null)
                .ThenBy(r => r.Group is null ? int.MaxValue : order.IndexOf(r.Group))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        bool changed = Apps.Count != sorted.Count
            || Apps.Where((r, i) => !ReferenceEquals(r, sorted[i])).Any();
        if (!changed) return;
        Apps.Clear();
        foreach (var r in sorted) Apps.Add(r);
    }

    // ---------------- user actions (UI thread) ----------------

    internal void OnChannelVolumeChanged(ChannelVm ch)
    {
        lock (Settings)
        {
            ch.Def.Volume = (float)ch.Volume;
            ch.Def.Muted = ch.Muted;
        }
        Save();
        _engine.ReconcileNow();
        OsdVolume?.Invoke(ch.Id, ch.Volume);
    }

    internal void OnChannelMuteChanged(ChannelVm ch)
    {
        lock (Settings)
        {
            ch.Def.Muted = ch.Muted;
            ch.Def.Volume = (float)ch.Volume;
        }
        Save();
        _engine.ReconcileNow();
        OsdMute?.Invoke(ch.Id, ch.Muted);
    }

    /// <summary>Hotkey volume nudge / mute toggle.</summary>
    public void NudgeVolume(string channelId, double delta, bool toggleMute = false)
    {
        var ch = Channels.FirstOrDefault(c => c.Id == channelId);
        if (ch is null) return;
        if (toggleMute) ch.Muted = !ch.Muted;
        else ch.Volume = Math.Clamp(ch.Volume + delta, 0, 1);
    }

    private void OnHotkey(string channelId, HotkeySlot slot)
    {
        float step;
        lock (Settings) step = Settings.HotkeyStep <= 0 ? 0.05f : Settings.HotkeyStep;
        NudgeVolume(channelId, slot switch
        {
            HotkeySlot.VolUp => +step,
            HotkeySlot.VolDown => -step,
            _ => 0,
        }, slot == HotkeySlot.Mute);
    }

    // ---------------- app assignment ----------------

    public void AssignExe(string exe, string channelId)
    {
        lock (Settings)
        {
            foreach (var c in Settings.Channels)
                c.Executables.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
            var target = Settings.Channels.FirstOrDefault(c => c.Id == channelId);
            if (target is not null && !target.Executables.Contains(exe, StringComparer.OrdinalIgnoreCase))
                target.Executables.Add(exe);
            if (!Settings.KnownApps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                Settings.KnownApps.Add(exe);
        }
        Save();
        _engine.ReconcileNow();
    }

    public void RemoveExe(string exe)
    {
        lock (Settings)
            foreach (var c in Settings.Channels)
                c.Executables.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
        Save();
        _engine.ReconcileNow();
    }

    /// <summary>Apps the user pinned via the Applications-panel + picker (kept visible even when not playing).</summary>
    public HashSet<string> PinnedApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Add an app row manually (the + picker): pinned + remembered.</summary>
    public void PinApp(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || exe == "system") return;
        PinnedApps.Add(exe);
        lock (Settings)
        {
            if (!Settings.KnownApps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                Settings.KnownApps.Add(exe);
        }
        Save();
        // show it immediately even if it has no live session (UI thread — Apps is UI-bound)
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Apps.Add(new AppRowVm(new SessionView(
            Key: "pinned:" + exe, Exe: exe, DisplayName: exe, Pid: 0, IsSystem: false,
            State: "Inactive", ChannelId: null, Volume: 1f, Mute: false))));
    }

    /// <summary>Apps available for the panel + picker: everything ever seen, not pinned yet.</summary>
    public List<string> AssignableApps(string channelId)
    {
        var ch = Channels.FirstOrDefault(c => c.Id == channelId);
        if (ch is null) return new List<string>();
        lock (Settings)
        {
            var taken = Settings.Channels
                .Where(c => c.Id != channelId)
                .SelectMany(c => c.Executables)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Settings.KnownApps
                .Where(a => !ch.Apps.Contains(a, StringComparer.OrdinalIgnoreCase) && !taken.Contains(a))
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // ---------------- hotkeys ----------------

    /// <summary>Set (or clear with null) a binding and re-register everything.</summary>
    public List<string> SetHotkey(string channelId, HotkeySlot slot, string? text)
    {
        var ch = Channels.FirstOrDefault(c => c.Id == channelId);
        if (ch is null) return new List<string>();
        lock (Settings) ch.Def.SetHotkey(slot, string.IsNullOrWhiteSpace(text) ? null : text);
        ch.HotkeysChangedExternally();
        Save();
        return RebindHotkeys();
    }

    public List<string> RebindHotkeys()
    {
        List<(string, HotkeySlot, string?)> bindings;
        lock (Settings)
        {
            bindings = Settings.Channels
                .SelectMany(c => new[]
                {
                    (c.Id, HotkeySlot.VolDown, c.GetHotkey(HotkeySlot.VolDown)),
                    (c.Id, HotkeySlot.VolUp, c.GetHotkey(HotkeySlot.VolUp)),
                    (c.Id, HotkeySlot.Mute, c.GetHotkey(HotkeySlot.Mute)),
                })
                .Where(b => b.Item3 is not null)
                .ToList();
        }
        return _hotkeys.ApplyAll(bindings);
    }

    // ---------------- outputs ----------------

    /// <summary>Rebuild the output list; selects the current default WITHOUT switching.</summary>
    public void RefreshOutputs()
    {
        var list = new List<OutputVm>();
        string? currentId = null;
        try
        {
            using var enumr = new MMDeviceEnumerator();
            var def = enumr.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            currentId = def?.ID;
            def?.Dispose();
            foreach (var dev in enumr.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new OutputVm(dev.ID, dev.FriendlyName));
                dev.Dispose();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Output enumeration failed: {ex.Message}";
            return;
        }

        Outputs.Clear();
        foreach (var o in list) Outputs.Add(o);
        var sel = Outputs.FirstOrDefault(o => o.Id == currentId) ?? Outputs.FirstOrDefault();
        if (sel is not null && !ReferenceEquals(sel, _selectedOutput))
            Set(ref _selectedOutput, sel, nameof(SelectedOutput));
    }

    /// <summary>Switch the Windows default output (same path for picker, tray menu, settings).</summary>
    public void SwitchOutput(OutputVm vm)
    {
        try
        {
            PolicyConfigApi.SetDefaultDeviceAllRoles(vm.Id);
            StatusText = $"Output → {vm.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not switch output: {ex.Message}";
        }
    }

    public void SetOutputSilently(OutputVm vm) => Set(ref _selectedOutput, vm, nameof(SelectedOutput));

    public void Save() => SettingsStore.Save(Settings);
}
