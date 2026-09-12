using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SONO.App;

/// <summary>Loads Assets/*.png icons once (cache) for use on buttons.
/// White source glyphs are recolored per-theme: white on dark themes, dark on light themes.</summary>
public static unsafe class UiIcon
{
    private static readonly Dictionary<string, Bitmap?> _cache = new();
    private static readonly Dictionary<(string, bool), Bitmap> _tinted = new();

    /// <summary>True when the current theme is light (icons must flip to dark glyphs).</summary>
    public static bool IsLightTheme
    {
        get
        {
            // parse the theme's Text color: light text ⇒ dark theme, dark text ⇒ light theme
            if (Color.TryParse(Themes.ThemeManager.Current.Text, out var t))
                return (t.R * 299 + t.G * 587 + t.B * 114) / 1000 > 128;
            return false;
        }
    }

    /// <summary>Recolor a white-glyph PNG: keep alpha, set RGB to the target color.
    /// Cache key includes the target shade so theme switches re-derive.</summary>
    public static Bitmap? GetTinted(string name, bool darkGlyph)
    {
        var key = (name, darkGlyph);
        if (_tinted.TryGetValue(key, out var cached)) return cached;
        var src = Get(name);
        if (src is null) return null;

        var target = darkGlyph ? Color.Parse("#3A3A3A") : Colors.White;
        var px = new byte[src.PixelSize.Width * src.PixelSize.Height * 4];
        unsafe
        {
            fixed (byte* dst = px)
            {
                src.CopyPixels(new PixelRect(0, 0, src.PixelSize.Width, src.PixelSize.Height),
                    (IntPtr)dst, px.Length, src.PixelSize.Width * 4);
            }
        }
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i + 0] = target.B;
            px[i + 1] = target.G;
            px[i + 2] = target.R;
            // alpha preserved
        }
        Bitmap tinted;
            fixed (byte* ptr = px)
            {
                tinted = new Bitmap(
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Unpremul,
                    (IntPtr)ptr,
                    src.PixelSize,
                    new Vector(96, 96),
                    src.PixelSize.Width * 4);
            }
        _tinted[key] = tinted;
        return tinted;
    }

    public static Bitmap? Get(string name)
    {
        if (_cache.TryGetValue(name, out var b)) return b;
        Bitmap? bmp = null;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"ic-{name}.png");
            if (File.Exists(path)) bmp = new Bitmap(path);
        }
        catch { }
        _cache[name] = bmp;
        return bmp;
    }

    /// <summary>Button whose Content is a centered, theme-tinted icon.</summary>
    public static Button IconButton(string name, int size = 16)
    {
        var img = new Image
        {
            Source = GetTinted(name, IsLightTheme),
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Button
        {
            Classes = { "sono" },
            Padding = new Thickness(0),
            Content = img,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }
}
