using System.Text.Json;
using SONO.App.Themes;

namespace SONO.App.Themes;

/// <summary>User-created theme palettes, stored in %APPDATA%\SONO\themes.json.
/// A custom theme is a full Palette clone with a "custom:" id prefix.</summary>
public static class CustomThemeStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SONO");
    private static string FilePath => Path.Combine(Dir, "themes.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<Palette> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<Palette>>(File.ReadAllText(FilePath), JsonOpts) ?? [];
                SONO.Core.Diagnostics.Log.Write($"custom themes loaded: {list.Count}");
                foreach (var t in list)
                    SONO.Core.Diagnostics.Log.Write($"  {t.Id} GroupBgs=[{string.Join("/", t.GroupBgs)}]");
                return list;
            }
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"custom themes load: {ex.Message}"); }
        return [];
    }

    public static void Save(IEnumerable<Palette> themes)
    {
        try
        {
            var customs = EditedOnly(themes);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(customs, JsonOpts));
            SONO.Core.Diagnostics.Log.Write($"custom themes saved: {string.Join(", ", customs.Select(t => t.Id + "=" + string.Join("/", t.GroupBgs)))}");
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"custom themes save: {ex.Message}"); }
    }

    /// <summary>What themes.json holds: every slot whose colors differ from its factory
    /// definition in the catalog (plus anything the catalog does not know). ALL colors are
    /// compared — the old check skipped Field and Muted, so editing only those was dropped.</summary>
    internal static List<Palette> EditedOnly(IEnumerable<Palette> themes) =>
        themes.Where(t =>
        {
            var factory = ThemeCatalog.All.FirstOrDefault(b => b.Id == t.Id);
            return factory is null || !factory.SameColorsAs(t);
        }).ToList();

    /// <summary>Clone a palette into an editable copy with a fresh custom id.</summary>
    public static Palette CloneAsCustom(Palette src, string name)
    {
        return new Palette
        {
            Id = "custom:" + Guid.NewGuid().ToString("N")[..12],
            Name = name,
            Bg = src.Bg, Card = src.Card, Elevated = src.Elevated, Field = src.Field,
            FieldFocus = src.FieldFocus, Chip = src.Chip, Border = src.Border,
            Text = src.Text, Muted = src.Muted, Accent = src.Accent, Danger = src.Danger,
            Swatches = src.Swatches.ToArray(),
            GroupBgs = src.GroupBgs.ToArray(),
            LogoUri = src.LogoUri,
        };
    }

    /// <summary>All built-ins + user themes (order: built-ins first). Every entry is the
    /// caller's own copy: the settings editor changes these palettes in place, and handing
    /// out the catalog's shared instances meant an edit also changed the factory reference
    /// that Save compares against — so the edit looked like "no change" and was never written.</summary>
    public static List<Palette> AllWithCustoms()
    {
        // file entries OVERRIDE catalog slots by Id (custom-1..3 are fixed slots, not additions)
        var overrides = new Dictionary<string, Palette>();
        foreach (var t in Load()) overrides[t.Id] = t;   // tolerate duplicate ids in a hand-edited file
        var result = new List<Palette>();
        foreach (var builtin in ThemeCatalog.All)
            result.Add(overrides.TryGetValue(builtin.Id, out var o) ? o : builtin.Clone());
        return result;
    }
}
