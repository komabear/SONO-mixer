using System.Collections.ObjectModel;
using SONO.Core.Audio;
using SONO.Core.Settings;

namespace SONO.App.ViewModels;

/// <summary>One group card: identity, volume, mute, hotkey bindings, member app chips.</summary>
public sealed class ChannelVm : ObservableObject
{
    public ChannelDefinition Def { get; }
    private readonly MixerVm _owner;

    public ChannelVm(ChannelDefinition def, MixerVm owner)
    {
        Def = def;
        _owner = owner;
        _volume = def.Volume;
        _muted = def.Muted;
        _volumePct = (int)Math.Round(def.Volume * 100);
        SyncHotkeyProps();   // boxes show the loaded bindings immediately
    }

    public string Id => Def.Id;
    public string Name => Def.Name;
    public string ColorHex => Def.ColorHex;

    private double _volume;
    /// <summary>User-driven volume (slider / hotkey). Applies to engine + fires OSD.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Set(ref _volume, v))
            {
                VolumePct = (int)Math.Round(v * 100);
                _owner.OnChannelVolumeChanged(this);
            }
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set { if (Set(ref _muted, value)) _owner.OnChannelMuteChanged(this); }
    }

    private int _volumePct;
    public int VolumePct { get => _volumePct; private set => Set(ref _volumePct, value); }

    private string? _volDownKey;
    public string? VolDownKey { get => _volDownKey; private set => Set(ref _volDownKey, value); }
    private string? _volUpKey;
    public string? VolUpKey { get => _volUpKey; private set => Set(ref _volUpKey, value); }
    private string? _muteKey;
    public string? MuteKey { get => _muteKey; private set => Set(ref _muteKey, value); }

    private void SyncHotkeyProps()
    {
        VolDownKey = Def.GetHotkey(HotkeySlot.VolDown);
        VolUpKey = Def.GetHotkey(HotkeySlot.VolUp);
        MuteKey = Def.GetHotkey(HotkeySlot.Mute);
    }

    public void HotkeysChangedExternally() => SyncHotkeyProps();

    // ---- app chips ----

    /// <summary>Executables assigned to this channel (live or not).</summary>
    public ObservableCollection<string> Apps { get; } = new();

    /// <summary>Reflect Def.Executables into the chips collection.</summary>
    public void SyncApps()
    {
        List<string> defs;
        lock (_owner.Settings) defs = Def.Executables.ToList();
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!defs.Contains(Apps[i], StringComparer.OrdinalIgnoreCase)) Apps.RemoveAt(i);
        foreach (var exe in defs)
            if (!Apps.Contains(exe, StringComparer.OrdinalIgnoreCase)) Apps.Add(exe);
    }

    /// <summary>Engine-side mirror WITHOUT re-applying (external-change path). Silent.</summary>
    public void UpdateFromEngine(float vol, bool muted)
    {
        var v = Math.Clamp(vol, 0, 1);
        if (Set(ref _volume, v, nameof(Volume)))
            VolumePct = (int)Math.Round(v * 100);
        Set(ref _muted, muted, nameof(Muted));
    }

    /// <summary>Raise UI refresh for user-side changes (e.g. hotkey nudge).</summary>
    public void NotifyUserChange() => Raise(nameof(Volume));
}
