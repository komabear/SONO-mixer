namespace SONO.App;

/// <summary>A full palette definition. All UI controls read colors at construction time,
/// so switching theme = save id + recreate the main form (audio routing survives).</summary>
public sealed class ThemeDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public Color Bg { get; init; }
    public Color Card { get; init; }
    public Color Elevated { get; init; }
    public Color Field { get; init; }
    public Color FieldFocus { get; init; }
    public Color Chip { get; init; }
    public Color Border { get; init; }
    public Color Text { get; init; }
    public Color Muted { get; init; }
    public Color Accent { get; init; }
    public Color Danger { get; init; }
    public string[] Palette { get; init; } = [];
    /// <summary>Optional mascot image (relative to app base dir) shown on the Applications panel.
    /// Default: the SONO Mixer logo — every theme gets it; individual themes (Miku) override.</summary>
    public string ImagePath { get; init; } = @"Assets\logo.png";
}

/// <summary>Live theme: static delegating properties keep every existing `Theme.X` call site working.</summary>
public static class Theme
{
    public static ThemeDef Current { get; private set; } = ThemeCatalog.Get("tokyo-night");

    public static Color Bg => Current.Bg;
    public static Color Card => Current.Card;
    public static Color Elevated => Current.Elevated;
    public static Color Field => Current.Field;
    public static Color FieldFocus => Current.FieldFocus;
    public static Color Chip => Current.Chip;
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color Muted => Current.Muted;
    public static Color Accent => Current.Accent;
    public static Color Danger => Current.Danger;
    public static Color CardInner => Current.Elevated;
    public static string[] Palette => Current.Palette;
    public static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

    public static void Apply(ThemeDef def) => Current = def;
}

public static class ThemeCatalog
{
    public static readonly IReadOnlyList<ThemeDef> All = new[]
    {
        new ThemeDef
        {
            Id = "tokyo-night", Name = "Tokyo Night",
            Bg = Color.FromArgb(22, 24, 34), Card = Color.FromArgb(31, 34, 48),
            Elevated = Color.FromArgb(38, 42, 61), Field = Color.FromArgb(44, 48, 70), FieldFocus = Color.FromArgb(56, 61, 88),
            Chip = Color.FromArgb(43, 47, 66), Border = Color.FromArgb(54, 58, 79),
            Text = Color.FromArgb(222, 225, 245), Muted = Color.FromArgb(128, 133, 158),
            Accent = Color.FromArgb(122, 162, 247), Danger = Color.FromArgb(247, 118, 142),
            Palette = new[] { "#7aa2f7", "#9ece6a", "#bb9af7", "#e0af68", "#f7768e", "#7dcfff", "#c0caf5", "#ff9e64" },
        },
        new ThemeDef
        {
            Id = "material-dark", Name = "Material Dark",
            Bg = Color.FromArgb(18, 18, 18), Card = Color.FromArgb(30, 30, 30),
            Elevated = Color.FromArgb(37, 37, 37), Field = Color.FromArgb(44, 44, 44), FieldFocus = Color.FromArgb(54, 54, 54),
            Chip = Color.FromArgb(50, 50, 50), Border = Color.FromArgb(60, 60, 60),
            Text = Color.FromArgb(230, 230, 230), Muted = Color.FromArgb(130, 130, 130),
            Accent = Color.FromArgb(130, 177, 255), Danger = Color.FromArgb(207, 102, 121),
            Palette = new[] { "#82B1FF", "#03DAC6", "#BB86FC", "#FFCF44", "#CF6679", "#69F0AE", "#FF9E80", "#B388FF" },
        },
        new ThemeDef
        {
            Id = "daylight", Name = "Material Daylight",
            Bg = Color.FromArgb(250, 250, 250), Card = Color.FromArgb(255, 255, 255),
            Elevated = Color.FromArgb(245, 245, 245), Field = Color.FromArgb(238, 238, 238), FieldFocus = Color.FromArgb(224, 224, 224),
            Chip = Color.FromArgb(224, 224, 224), Border = Color.FromArgb(224, 224, 224),
            Text = Color.FromArgb(33, 33, 33), Muted = Color.FromArgb(117, 117, 117),
            Accent = Color.FromArgb(98, 0, 238), Danger = Color.FromArgb(176, 0, 32),
            Palette = new[] { "#6200EE", "#03DAC6", "#E91E63", "#FF9800", "#F44336", "#4CAF50", "#2196F3", "#795548" },
        },
        new ThemeDef
        {
            Id = "nord", Name = "Nord",
            Bg = Color.FromArgb(46, 52, 64), Card = Color.FromArgb(59, 66, 82),
            Elevated = Color.FromArgb(67, 76, 94), Field = Color.FromArgb(76, 86, 106), FieldFocus = Color.FromArgb(88, 99, 122),
            Chip = Color.FromArgb(76, 86, 106), Border = Color.FromArgb(76, 86, 106),
            Text = Color.FromArgb(236, 239, 244), Muted = Color.FromArgb(136, 148, 169),
            Accent = Color.FromArgb(136, 192, 208), Danger = Color.FromArgb(191, 97, 106),
            Palette = new[] { "#88C0D0", "#A3BE8C", "#B48EAD", "#EBCB8B", "#BF616A", "#8FBCBB", "#81A1C1", "#D08770" },
        },
        new ThemeDef
        {
            Id = "dracula", Name = "Dracula",
            Bg = Color.FromArgb(40, 42, 54), Card = Color.FromArgb(52, 55, 70),
            Elevated = Color.FromArgb(60, 63, 81), Field = Color.FromArgb(68, 71, 90), FieldFocus = Color.FromArgb(80, 84, 106),
            Chip = Color.FromArgb(68, 71, 90), Border = Color.FromArgb(68, 71, 90),
            Text = Color.FromArgb(248, 248, 242), Muted = Color.FromArgb(98, 114, 164),
            Accent = Color.FromArgb(189, 147, 249), Danger = Color.FromArgb(255, 85, 85),
            Palette = new[] { "#BD93F9", "#50FA7B", "#FF79C6", "#8BE9FD", "#F1FA8C", "#FFB86C", "#FF5555", "#6272A4" },
        },
        new ThemeDef
        {
            Id = "solarized-dark", Name = "Solarized Dark",
            Bg = Color.FromArgb(0, 43, 54), Card = Color.FromArgb(7, 54, 66),
            Elevated = Color.FromArgb(10, 67, 82), Field = Color.FromArgb(14, 74, 89), FieldFocus = Color.FromArgb(20, 88, 105),
            Chip = Color.FromArgb(14, 74, 89), Border = Color.FromArgb(20, 88, 105),
            Text = Color.FromArgb(238, 232, 213), Muted = Color.FromArgb(131, 148, 150),
            Accent = Color.FromArgb(38, 139, 210), Danger = Color.FromArgb(220, 50, 47),
            Palette = new[] { "#268BD2", "#859900", "#6C71C4", "#B58900", "#DC322F", "#2AA198", "#CB4B16", "#D33682" },
        },
        new ThemeDef
        {
            Id = "gruvbox-dark", Name = "Gruvbox Dark",
            Bg = Color.FromArgb(40, 40, 40), Card = Color.FromArgb(60, 56, 54),
            Elevated = Color.FromArgb(50, 48, 47), Field = Color.FromArgb(69, 64, 61), FieldFocus = Color.FromArgb(80, 73, 69),
            Chip = Color.FromArgb(80, 73, 69), Border = Color.FromArgb(80, 73, 69),
            Text = Color.FromArgb(251, 241, 199), Muted = Color.FromArgb(146, 131, 116),
            Accent = Color.FromArgb(131, 165, 152), Danger = Color.FromArgb(251, 73, 52),
            Palette = new[] { "#83A598", "#B8BB26", "#D3869B", "#FABD2F", "#FB4934", "#8EC07C", "#FE8019", "#A89984" },
        },
        new ThemeDef
        {
            Id = "catppuccin-mocha", Name = "Catppuccin Mocha",
            Bg = Color.FromArgb(30, 30, 46), Card = Color.FromArgb(38, 38, 55),
            Elevated = Color.FromArgb(49, 50, 68), Field = Color.FromArgb(57, 58, 78), FieldFocus = Color.FromArgb(69, 71, 90),
            Chip = Color.FromArgb(57, 58, 78), Border = Color.FromArgb(69, 71, 90),
            Text = Color.FromArgb(205, 214, 244), Muted = Color.FromArgb(127, 132, 156),
            Accent = Color.FromArgb(137, 180, 250), Danger = Color.FromArgb(243, 139, 168),
            Palette = new[] { "#89B4FA", "#A6E3A1", "#CBA6F7", "#F9E2AF", "#F38BA8", "#94E2D5", "#F5C2E7", "#FAB387" },
        },
        new ThemeDef
        {
            Id = "one-dark", Name = "One Dark",
            Bg = Color.FromArgb(33, 37, 43), Card = Color.FromArgb(44, 49, 58),
            Elevated = Color.FromArgb(50, 56, 66), Field = Color.FromArgb(58, 63, 75), FieldFocus = Color.FromArgb(70, 76, 90),
            Chip = Color.FromArgb(58, 63, 75), Border = Color.FromArgb(70, 76, 90),
            Text = Color.FromArgb(220, 223, 228), Muted = Color.FromArgb(127, 132, 142),
            Accent = Color.FromArgb(97, 175, 239), Danger = Color.FromArgb(224, 108, 117),
            Palette = new[] { "#61AFEF", "#98C379", "#C678DD", "#E5C07B", "#E06C75", "#56B6C2", "#D19A66", "#7F848E" },
        },
        new ThemeDef
        {
            Id = "miku", Name = "Hatsune Miku",
            Bg = Color.FromArgb(0x37, 0x3b, 0x3e), Card = Color.FromArgb(0x43, 0x48, 0x4c),
            Elevated = Color.FromArgb(0x4b, 0x51, 0x55), Field = Color.FromArgb(0x52, 0x58, 0x5c), FieldFocus = Color.FromArgb(0x5d, 0x64, 0x68),
            Chip = Color.FromArgb(0x52, 0x58, 0x5c), Border = Color.FromArgb(0x5d, 0x64, 0x68),
            Text = Color.FromArgb(0xbe, 0xc8, 0xd1), Muted = Color.FromArgb(0x8b, 0x97, 0x9e),
            Accent = Color.FromArgb(0x86, 0xce, 0xcb), Danger = Color.FromArgb(0xe1, 0x28, 0x85),
            Palette = new[] { "#86CECB", "#137A7F", "#BEC8D1", "#E12885", "#5B8FA8", "#7DE8DD", "#9FB4BD", "#4FC1E9" },
            ImagePath = @"Assets\logo-miku.png",
        },
    };

    public static ThemeDef Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
