using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SONO.Core.Diagnostics;

namespace SONO.App.Themes;

/// <summary>Loads a Palette into Avalonia's resource layer as dynamic resources so every
/// control restyles live (no window recreation, unlike the old WinForms port).</summary>
public static class ThemeManager
{
    private static Palette? _current;
    public static Palette Current => _current ?? ThemeCatalog.Default;
    /// <summary>Fires after Apply() so every live surface (main window, cards, rows) repaints.</summary>
    public static event Action? ThemeChanged;

    public static void Apply(Palette p)
    {
        _current = p;
        if (Application.Current is null) return;
        // Fluent's templates (text boxes, combos, buttons, checkbox glyphs) color themselves
        // from the ACTIVE THEME VARIANT, not from our brushes. A light palette under the
        // Dark variant renders light-on-light / dark-on-dark. Flip the variant by luminance:
        if (Color.Parse(p.Bg) is var bg &&
            (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 > 128)
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        else
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        var r = Application.Current.Resources;
        r["SonoBg"] = Color.Parse(p.Bg);
        r["SonoCard"] = Color.Parse(p.Card);
        r["SonoElevated"] = Color.Parse(p.Elevated);
        r["SonoField"] = Color.Parse(p.Field);
        r["SonoFieldFocus"] = Color.Parse(p.FieldFocus);
        r["SonoChip"] = Color.Parse(p.Chip);
        r["SonoBorder"] = Color.Parse(p.Border);
        r["SonoText"] = Color.Parse(p.Text);
        r["SonoMuted"] = Color.Parse(p.Muted);
        r["SonoAccent"] = Color.Parse(p.Accent);
        r["SonoDanger"] = Color.Parse(p.Danger);
        r["SonoAccentBrush"] = new SolidColorBrush(Color.Parse(p.Accent));
        r["SonoBgBrush"] = new SolidColorBrush(Color.Parse(p.Bg));
        r["SonoCardBrush"] = new SolidColorBrush(Color.Parse(p.Card));
        r["SonoElevatedBrush"] = new SolidColorBrush(Color.Parse(p.Elevated));
        r["SonoFieldBrush"] = new SolidColorBrush(Color.Parse(p.Field));
        r["SonoBorderBrush"] = new SolidColorBrush(Color.Parse(p.Border));
        r["SonoTextBrush"] = new SolidColorBrush(Color.Parse(p.Text));
        r["SonoMutedBrush"] = new SolidColorBrush(Color.Parse(p.Muted));
        r["SonoDangerBrush"] = new SolidColorBrush(Color.Parse(p.Danger));
        // channel swatches: fixed indices into the palette
        if (p.Swatches.Length >= 4)
        {
            r["SonoGameBrush"] = new SolidColorBrush(Color.Parse(p.Swatches[0]));
            r["SonoChatBrush"] = new SolidColorBrush(Color.Parse(p.Swatches[1]));
            r["SonoMediaBrush"] = new SolidColorBrush(Color.Parse(p.Swatches[2]));
            r["SonoAuxBrush"] = new SolidColorBrush(Color.Parse(p.Swatches[3]));
        }
        r["SonoLogoUri"] = p.LogoUri;
        try
        {
            ThemeChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // one throwing subscriber must not starve the rest of the repaint chain
            SONO.Core.Diagnostics.Log.Write("ThemeChanged subscriber failed: " + ex);
        }
    }
}
