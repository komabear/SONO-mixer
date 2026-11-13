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
    private static readonly Dictionary<(string, string), Bitmap> _tinted = new();

    /// <summary>True when the current theme is light (icons must flip to dark glyphs).</summary>
    public static bool IsLightTheme
    {
        get
        {
            // measure the theme's BACKGROUND luminance: light bg ⇒ light theme (dark glyphs)
            if (Color.TryParse(Themes.ThemeManager.Current.Bg, out var b))
                return (b.R * 299 + b.G * 587 + b.B * 114) / 1000 > 128;
            return false;
        }
    }

    /// <summary>The glyph color for the CURRENT theme: the theme's own Text color
    /// (contrast-correct by definition), falling back to the light/dark flip.</summary>
    public static Color ThemeGlyphColor
    {
        get
        {
            if (Color.TryParse(Themes.ThemeManager.Current.Text, out var t)) return t;
            return IsLightTheme ? Color.Parse("#3A3A3A") : Colors.White;
        }
    }

    /// <summary>Recolor a white-glyph PNG: keep alpha, set RGB to the target color.
    /// Cache key includes the target color so theme switches re-derive.</summary>
    public static Bitmap? GetTinted(string name, bool darkGlyph)
    {
        return GetTinted(name, ThemeGlyphColor);
    }

    public static Bitmap? GetTinted(string name, Color target)
    {
        var key = (name, target.ToString());
        if (_tinted.TryGetValue(key, out var cached)) return cached;
        var src = Get(name);
        if (src is null) return null;
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
        // dragging the Text color in the theme editor asks for a new tint on every step —
        // keep the cache bounded (images already showing a bitmap keep their own reference)
        if (_tinted.Count >= 48) _tinted.Clear();
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

    /// <summary>Button whose Content is a centered, theme-tinted icon. Re-tints on theme change.</summary>
    public static Button IconButton(string name, int size = 16)
    {
        var img = new Image
        {
            Source = GetTinted(name, ThemeGlyphColor),
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Classes = { "sono" },
            Padding = new Thickness(0),
            Content = img,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Live re-tint when the theme changes (glyph color = theme Text) — but only while the
        // button is in a window. ThemeChanged is a static event: a subscription that outlives
        // the button keeps the button, and the whole app row around it, alive for the rest
        // of the process. Rows come and go every time an app starts or stops playing.
        void Retint() => img.Source = GetTinted(name, ThemeGlyphColor);
        Action onThemeChanged = () => Avalonia.Threading.Dispatcher.UIThread.Post(Retint);
        btn.AttachedToVisualTree += (_, _) =>
        {
            Themes.ThemeManager.ThemeChanged -= onThemeChanged;
            Themes.ThemeManager.ThemeChanged += onThemeChanged;
            Retint();   // catch up with a theme change that happened while detached
        };
        btn.DetachedFromVisualTree += (_, _) => Themes.ThemeManager.ThemeChanged -= onThemeChanged;
        return btn;
    }
}
