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

    /// <summary>OSD anchor: "off" or one of the 9 screen positions
    /// (top-left/top/top-right/left/center/right/bottom-left/bottom/bottom-right).</summary>
    public string OsdAnchor { get; set; } = "bottom-right";

    /// <summary>Endpoint ids hidden from the main window's OUTPUT dropdown.
    /// The current default output is always shown regardless.</summary>
    public List<string> HiddenOutputs { get; set; } = new();

    /// <summary>Global hotkey for switching to the previous visible output ("Ctrl+Alt+Left" style text; empty = unbound).</summary>
    public string? OutputPrevHotkey { get; set; }

    /// <summary>Global hotkey for switching to the next visible output; empty = unbound.</summary>
    public string? OutputNextHotkey { get; set; }
    /// <summary>True once the app has written the autostart Run key for the first time —
    /// prevents re-asserting it (and re-pointing it at a different exe path) every launch.</summary>
    public bool AutostartConfigured { get; set; }
    public List<ChannelDefinition> Channels { get; set; } = new();
}

public static class SettingsStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SONO");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly object WriteGate = new();
    private static long _snapshots;     // stamps each serialized snapshot (taken under the settings lock)
    private static long _lastWritten;   // newest stamp that reached the disk (under WriteGate)
    /// <summary>Set when an existing settings file could not be READ (as opposed to parsed).
    /// This run then never writes, so a file that was only locked for a moment is not
    /// replaced by the defaults the app had to start with.</summary>
    private static bool _writesDisabled;

    public static AppSettings Load() => LoadFrom(FilePath);

    /// <summary>Persist the settings. Never throws — a failed write is logged and reported
    /// as false (this is called from UI event handlers and from a native hotkey callback,
    /// where an I/O exception would take the app down).</summary>
    public static bool Save(AppSettings s) => SaveTo(s, FilePath);

    internal static AppSettings LoadFrom(string path)
    {
        var (text, missing) = ReadWithRetry(path);
        if (!missing)
        {
            if (text is null)
            {
                // exists but unreadable right now (held by a backup/antivirus tool, access denied)
                _writesDisabled = true;
                Diagnostics.Log.Write("settings: the file exists but could not be read — running on defaults, nothing will be saved this session");
                return Defaults();
            }
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts) ?? throw new JsonException("empty document");
                bool repaired = Normalize(s);
                // migrate: mic channels removed in v0.2 (output-focused)
                int before = s.Channels.Count;
                s.Channels.RemoveAll(c => c.Kind != ChannelKind.Group);
                if (s.Channels.Count == 0)
                {
                    s.Channels = Defaults().Channels;   // the mixer is built around its four groups
                    repaired = true;
                }
                if (repaired || s.Channels.Count != before) SaveTo(s, path);
                return s;
            }
            catch (Exception ex)
            {
                // Corrupt (or hand-edited into invalid JSON): keep it for recovery. It used to
                // be overwritten with defaults on the spot — every group, hotkey and hidden
                // output gone, with no copy and no trace.
                string bad = Path.Combine(Path.GetDirectoryName(path) ?? "", "settings.bad.json");
                try { File.Move(path, bad, overwrite: true); }
                catch (Exception moveEx) { Diagnostics.Log.Write($"settings: could not set the unreadable file aside: {moveEx.Message}"); }
                Diagnostics.Log.Write($"settings: settings.json could not be parsed ({ex.Message}) — kept as settings.bad.json, starting from defaults");
            }
        }
        var d = Defaults();
        SaveTo(d, path);
        return d;
    }

    /// <summary>Read the file, riding out a short lock. Missing = there is no file (first run);
    /// Text null with Missing false = it is there but cannot be read.</summary>
    private static (string? Text, bool Missing) ReadWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return (null, true);
                return (File.ReadAllText(path), false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                {
                    Diagnostics.Log.Write($"settings: read failed: {ex.Message}");
                    return (null, false);
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>A hand-edited file can null out a list; the app assumes they all exist.</summary>
    private static bool Normalize(AppSettings s)
    {
        bool repaired = false;
        if (s.KnownApps is null) { s.KnownApps = new(); repaired = true; }
        if (s.HiddenOutputs is null) { s.HiddenOutputs = new(); repaired = true; }
        if (s.Channels is null) { s.Channels = new(); repaired = true; }
        if (s.Channels.RemoveAll(c => c is null) > 0) repaired = true;
        foreach (var c in s.Channels)
        {
            if (c.Executables is null) { c.Executables = new(); repaired = true; }
            if (c.Hotkeys is null) { c.Hotkeys = new(StringComparer.Ordinal); repaired = true; }
        }
        return repaired;
    }

    internal static bool SaveTo(AppSettings s, string path)
    {
        string? tmp = null;
        try
        {
            if (_writesDisabled) return false;

            // Snapshot under the lock every writer of the settings object takes. The serializer
            // walks the app and channel lists; walking them while the UI thread added an
            // entry threw "collection was modified" on the save timer's thread, where the
            // exception was swallowed and the save silently lost.
            string json;
            long stamp;
            lock (s)
            {
                json = JsonSerializer.Serialize(s, JsonOpts);
                stamp = Interlocked.Increment(ref _snapshots);
            }

            lock (WriteGate)
            {
                if (stamp < _lastWritten) return true;   // a newer snapshot is already on disk

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Write beside the target, flush, then swap it in. A crash or power cut during
                // the write leaves the previous file intact instead of a truncated one
                // (which the next launch then treated as corrupt).
                tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(System.Text.Encoding.UTF8.GetBytes(json));
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
                _lastWritten = stamp;
            }
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Write($"settings save failed: {ex.Message}");
            try { if (tmp is not null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
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
                Mk("Chat", "#9ece6a"),
                Mk("Media", "#bb9af7"),
                Mk("Aux", "#e0af68"),
            },
        };
    }
}
