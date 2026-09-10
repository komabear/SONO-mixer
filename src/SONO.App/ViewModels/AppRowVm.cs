using SONO.Core.Audio;

namespace SONO.App.ViewModels;

/// <summary>One app row in the Applications panel: name + live volume bar + group tag.
/// Informative only — session volume is owned by the engine (group volume model).</summary>
public sealed class AppRowVm : ObservableObject
{
    public string Exe { get; }
    private string _name;
    public string Name { get => _name; private set => Set(ref _name, value); }

    public AppRowVm(SessionView s)
    {
        Exe = s.Exe;
        _name = s.DisplayName;
    }

    private double _sessionVol;
    /// <summary>Live session volume 0..1 (read-only display).</summary>
    public double SessionVol { get => _sessionVol; set => Set(ref _sessionVol, value); }

    private bool _sessionMuted;
    public bool SessionMuted { get => _sessionMuted; set => Set(ref _sessionMuted, value); }

    private string? _group;
    /// <summary>Group id if this exe is claimed by a channel, else null.</summary>
    public string? Group
    {
        get => _group;
        set
        {
            if (Set(ref _group, value))
            {
                Raise(nameof(HasGroup));
                Raise(nameof(GroupName));
            }
        }
    }

    public bool HasGroup => Group is not null;

    private string _groupName = "";
    public string GroupName { get => _groupName; private set => Set(ref _groupName, value); }

    public void Update(SessionView s, string? groupName)
    {
        Name = s.DisplayName;
        SessionVol = s.Volume;
        SessionMuted = s.Mute;
        Group = s.ChannelId;
        GroupName = groupName ?? "";
    }
}
