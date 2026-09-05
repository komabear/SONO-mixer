namespace SONO.App;

/// <summary>Dark palette shared by the mixer UI.</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(22, 24, 34);
    public static readonly Color Card = Color.FromArgb(31, 34, 48);
    public static readonly Color CardInner = Color.FromArgb(26, 28, 40);
    public static readonly Color Border = Color.FromArgb(54, 58, 79);
    public static readonly Color Chip = Color.FromArgb(43, 47, 66);
    public static readonly Color Text = Color.FromArgb(222, 225, 245);
    public static readonly Color Muted = Color.FromArgb(128, 133, 158);
    public static readonly Color Accent = Color.FromArgb(122, 162, 247);
    public static readonly Color Danger = Color.FromArgb(247, 118, 142);

    // Material-style tonal surfaces (dark theme: elevated = lighter)
    public static readonly Color Elevated = Color.FromArgb(38, 42, 61);      // inner surfaces (apps area)
    public static readonly Color Field = Color.FromArgb(44, 48, 70);         // filled text fields
    public static readonly Color FieldFocus = Color.FromArgb(56, 61, 88);    // focused text field

    public static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly string[] Palette =
    {
        "#7aa2f7", "#9ece6a", "#bb9af7", "#e0af68", "#f7768e", "#7dcfff", "#c0caf5", "#ff9e64",
    };
}
