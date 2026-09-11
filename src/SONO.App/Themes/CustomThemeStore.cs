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
                return JsonSerializer.Deserialize<List<Palette>>(File.ReadAllText(FilePath), JsonOpts) ?? [];
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"custom themes load: {ex.Message}"); }
        return [];
    }

    public static void Save(IEnumerable<Palette> themes)
    {
        try
        {
            // invariant: themes.json holds ONLY custom slots (never built-ins)
            var customs = themes.Where(t => t.Id.StartsWith("custom-")).ToList();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(customs, JsonOpts));
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"custom themes save: {ex.Message}"); }
    }

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

    /// <summary>All built-ins + user themes (order: built-ins first).</summary>
    public static List<Palette> AllWithCustoms()
    {
        // file entries OVERRIDE catalog slots by Id (custom-1..3 are fixed slots, not additions)
        var overrides = Load().ToDictionary(t => t.Id);
        var result = new List<Palette>();
        foreach (var builtin in ThemeCatalog.All)
            result.Add(overrides.TryGetValue(builtin.Id, out var o) ? o : builtin);
        return result;
    }
}
