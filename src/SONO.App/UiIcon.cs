using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SONO.App;

/// <summary>Loads Assets/*.png icons once (cache) for use on buttons.</summary>
public static class UiIcon
{
    private static readonly Dictionary<string, Bitmap?> _cache = new();

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

    /// <summary>Button whose Content is a centered 16px icon.</summary>
    public static Button IconButton(string name, int size = 16)
    {
        var img = new Image
        {
            Source = Get(name),
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.92,
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
