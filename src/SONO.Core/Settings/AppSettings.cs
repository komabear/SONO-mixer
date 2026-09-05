using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SONO.Core.Audio;

namespace SONO.Core.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    /// <summary>Volume change per hotkey press (0..1).</summary>
    public float HotkeyStep { get; set; } = 0.05f;

    /// <summary>Apps ever seen live or added by hand, so they can be pre-assigned before running.</summary>
    public List<string> KnownApps { get; set; } = new();

    /// <summary>Real output device the mixed channels play through (endpoint id).</summary>
    public string? RealOutputId { get; set; }

    /// <summary>Active UI theme id (see ThemeCatalog).</summary>
    public string ThemeId { get; set; } = "tokyo-night";
    public List<ChannelDefinition> Channels { get; set; } = new();
}

public static class SettingsStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SONO");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? Defaults();
                // migrate: mic channels removed in v0.2 (output-focused)
                int before = s.Channels.Count;
                s.Channels.RemoveAll(c => c.Kind != ChannelKind.Group);
                if (s.Channels.Count != before) Save(s);
                return s;
            }
        }
        catch { /* corrupt file → fall through to defaults */ }
        var d = Defaults();
        Save(d);
        return d;
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
    }

    public static string FilePathForDiagnostics => FilePath;

    public static AppSettings Defaults()
    {
        static ChannelDefinition Mk(string name, string color,
            ChannelKind kind = ChannelKind.Group, params string[] exes) => new()
        {
            Name = name,
            ColorHex = color,
            Kind = kind,
            Executables = exes.ToList(),
        };

        return new AppSettings
        {
            Channels =
            {
                Mk("Game", "#7aa2f7"),
                Mk("Chat", "#9ece6a", exes: "discord.exe"),
                Mk("Media", "#bb9af7", exes: "spotify.exe"),
                Mk("Aux", "#e0af68"),
            },
        };
    }
}
