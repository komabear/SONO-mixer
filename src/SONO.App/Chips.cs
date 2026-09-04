namespace SONO.App;

/// <summary>Draggable app "chips" — the currency of app→channel assignment.</summary>
internal static class Chips
{
    public const string Format = "SONO_EXE";

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge", ["chrome"] = "Chrome", ["firefox"] = "Firefox",
        ["spotify"] = "Spotify", ["discord"] = "Discord", ["steam"] = "Steam",
        ["code"] = "VS Code", ["devenv"] = "Visual Studio", ["telegram"] = "Telegram",
        ["whatsapp"] = "WhatsApp", ["obs64"] = "OBS", ["ffmpeg"] = "FFmpeg",
        ["system"] = "System Sounds", ["youtube music"] = "YouTube Music",
        ["overwatch"] = "Overwatch", ["steam"] = "Steam", ["opera"] = "Opera",
        ["svchost"] = "Windows Audio", ["explorer"] = "Explorer",
    };

    private static readonly ToolTip Tips = new();

    public static string Pretty(string exe)
    {
        var base_ = exe == "system" ? "system" : Path.GetFileNameWithoutExtension(exe);
        if (Titles.TryGetValue(base_, out var t)) return t;
        return base_.Length == 0 ? base_ : char.ToUpperInvariant(base_[0]) + base_[1..];
    }

    /// <summary>Create a chip label for an exe. onRemove != null enables double-click-to-remove.</summary>
    public static Label Make(string exe, Action<string>? onRemove = null)
    {
        var l = new Label
        {
            Text = Pretty(exe) + (onRemove is not null ? "  ✕" : ""),
            AutoSize = true,
            ForeColor = Theme.Text,
            BackColor = Theme.Chip,
            Padding = new Padding(7, 5, 7, 5),
            Margin = new Padding(3),
            Cursor = Cursors.Hand,
            Tag = exe,
        };
        Tips.SetToolTip(l, exe + (onRemove is not null
            ? "\nDrag to another channel · double-click to remove"
            : "\nDrag onto a channel to assign it"));
        var down = Point.Empty;
        l.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) down = e.Location; };
        l.MouseMove += (_, e) =>
        {
            if (e.Button == MouseButtons.Left &&
                Math.Abs(e.X - down.X) + Math.Abs(e.Y - down.Y) > 6)
                l.DoDragDrop(new DataObject(Format, exe), DragDropEffects.Move);
        };
        if (onRemove is not null) l.DoubleClick += (_, _) => onRemove(exe);
        return l;
    }
}
