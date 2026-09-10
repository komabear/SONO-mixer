using Avalonia.Media;

namespace SONO.App.Themes;

/// <summary>A full palette definition. Colors flow into Avalonia DynamicResource keys,
/// so switching theme = swap the resource dictionary (no window recreation needed).</summary>
public sealed class Palette
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Bg { get; init; } = "";
    public string Card { get; init; } = "";
    public string Elevated { get; init; } = "";
    public string Field { get; init; } = "";
    public string FieldFocus { get; init; } = "";
    public string Chip { get; init; } = "";
    public string Border { get; init; } = "";
    public string Text { get; init; } = "";
    public string Muted { get; init; } = "";
    public string Accent { get; init; } = "";
    public string Danger { get; init; } = "";
    public string[] Swatches { get; init; } = [];
    /// <summary>Group box background colors (Game/Chat/Media/Aux) — blue/green/pink/yellow,
    /// tuned per theme. Drag-over = lighter variant; slider fill = darker variant.</summary>
    public string[] GroupBgs { get; init; } = ["#5B8DEF", "#65D97A", "#F06BB3", "#F5C453"];
    /// <summary>Logo shown on the Applications panel (avares URI); Miku overrides.</summary>
    public string LogoUri { get; init; } = "avares://SONO.App/Assets/logo.png";
}

public static class ThemeCatalog
{
    public static readonly Palette Default = new()
    {
        Id = "tokyo-night", Name = "Tokyo Night",
        Bg = "#161822", Card = "#1F2230", Elevated = "#262A3D", Field = "#2C3046", FieldFocus = "#383D58",
        Chip = "#2B2F42", Border = "#363A4F", Text = "#DEE1F5", Muted = "#80859E",
        Accent = "#7AA2F7", Danger = "#F7768E",
        Swatches = ["#7aa2f7", "#9ece6a", "#bb9af7", "#e0af68", "#f7768e", "#7dcfff", "#c0caf5", "#ff9e64"],
            GroupBgs = ["#5B8DEF", "#65D97A", "#F06BB3", "#F5C453"],
    };

    public static readonly IReadOnlyList<Palette> All =
    [
        Default,
        new Palette
        {
            Id = "material-dark", Name = "Material Dark",
            Bg = "#121212", Card = "#1E1E1E", Elevated = "#252525", Field = "#2C2C2C", FieldFocus = "#363636",
            Chip = "#323232", Border = "#3C3C3C", Text = "#E6E6E6", Muted = "#828282",
            Accent = "#82B1FF", Danger = "#CF6679",
            Swatches = ["#82B1FF", "#03DAC6", "#BB86FC", "#FFCF44", "#CF6679", "#69F0AE", "#FF9E80", "#B388FF"],
            GroupBgs = ["#6FA3FF", "#5FD68B", "#FF77B0", "#FFD35C"],
        },
        new Palette
        {
            Id = "daylight", Name = "Material Daylight",
            Bg = "#FAFAFA", Card = "#FFFFFF", Elevated = "#F5F5F5", Field = "#EEEEEE", FieldFocus = "#E0E0E0",
            Chip = "#E0E0E0", Border = "#E0E0E0", Text = "#212121", Muted = "#757575",
            Accent = "#6200EE", Danger = "#B00020",
            Swatches = ["#6200EE", "#03DAC6", "#E91E63", "#FF9800", "#F44336", "#4CAF50", "#2196F3", "#795548"],
            GroupBgs = ["#9DBEF5", "#A8E6C3", "#F5BCDD", "#F7E3AE"],
        },
        new Palette
        {
            Id = "nord", Name = "Nord",
            Bg = "#2E3440", Card = "#3B4252", Elevated = "#434C5E", Field = "#4C566A", FieldFocus = "#58637A",
            Chip = "#4C566A", Border = "#4C566A", Text = "#ECEFF4", Muted = "#8894AB",
            Accent = "#88C0D0", Danger = "#BF616A",
            Swatches = ["#88C0D0", "#A3BE8C", "#B48EAD", "#EBCB8B", "#BF616A", "#8FBCBB", "#81A1C1", "#D08770"],
            GroupBgs = ["#88C0D0", "#A3BE8C", "#B48EAD", "#EBCB8B"],
        },
        new Palette
        {
            Id = "dracula", Name = "Dracula",
            Bg = "#282A36", Card = "#343746", Elevated = "#3C3F51", Field = "#44475A", FieldFocus = "#50546A",
            Chip = "#44475A", Border = "#44475A", Text = "#F8F8F2", Muted = "#6272A4",
            Accent = "#BD93F9", Danger = "#FF5555",
            Swatches = ["#BD93F9", "#50FA7B", "#FF79C6", "#8BE9FD", "#F1FA8C", "#FFB86C", "#FF5555", "#6272A4"],
            GroupBgs = ["#BD93F9", "#50FA7B", "#FF79C6", "#F1FA8C"],
        },
        new Palette
        {
            Id = "solarized-dark", Name = "Solarized Dark",
            Bg = "#002B36", Card = "#073642", Elevated = "#0A4252", Field = "#0E4A59", FieldFocus = "#145869",
            Chip = "#0E4A59", Border = "#145869", Text = "#EEE8D5", Muted = "#839496",
            Accent = "#268BD2", Danger = "#DC322F",
            Swatches = ["#268BD2", "#859900", "#6C71C4", "#B58900", "#DC322F", "#2AA198", "#CB4B16", "#D33682"],
            GroupBgs = ["#268BD2", "#859900", "#D33682", "#B58900"],
        },
        new Palette
        {
            Id = "gruvbox-dark", Name = "Gruvbox Dark",
            Bg = "#282828", Card = "#3C3836", Elevated = "#32302F", Field = "#45403D", FieldFocus = "#504945",
            Chip = "#504945", Border = "#504945", Text = "#FBF1C7", Muted = "#928374",
            Accent = "#83A598", Danger = "#FB4934",
            Swatches = ["#83A598", "#B8BB26", "#D3869B", "#FABD2F", "#FB4934", "#8EC07C", "#FE8019", "#A89984"],
            GroupBgs = ["#83A598", "#B8BB26", "#D3869B", "#FABD2F"],
        },
        new Palette
        {
            Id = "catppuccin-mocha", Name = "Catppuccin Mocha",
            Bg = "#1E1E2E", Card = "#262637", Elevated = "#313244", Field = "#393A4E", FieldFocus = "#45475A",
            Chip = "#393A4E", Border = "#45475A", Text = "#CDD6F4", Muted = "#7F849C",
            Accent = "#89B4FA", Danger = "#F38BA8",
            Swatches = ["#89B4FA", "#A6E3A1", "#CBA6F7", "#F9E2AF", "#F38BA8", "#94E2D5", "#F5C2E7", "#FAB387"],
            GroupBgs = ["#89B4FA", "#A6E3A1", "#F5C2E7", "#F9E2AF"],
        },
        new Palette
        {
            Id = "one-dark", Name = "One Dark",
            Bg = "#21252B", Card = "#2C313A", Elevated = "#323842", Field = "#3A3F4B", FieldFocus = "#464C5A",
            Chip = "#3A3F4B", Border = "#464C5A", Text = "#DCDFE4", Muted = "#7F848E",
            Accent = "#61AFEF", Danger = "#E06C75",
            Swatches = ["#61AFEF", "#98C379", "#C678DD", "#E5C07B", "#E06C75", "#56B6C2", "#D19A66", "#7F848E"],
            GroupBgs = ["#61AFEF", "#98C379", "#E06C75", "#E5C07B"],
        },
        new Palette
        {
            Id = "miku", Name = "Hatsune Miku",
            Bg = "#373B3E", Card = "#43484C", Elevated = "#4B5155", Field = "#52585C", FieldFocus = "#5D6468",
            Chip = "#52585C", Border = "#5D6468", Text = "#BEC8D1", Muted = "#8B979E",
            Accent = "#86CECB", Danger = "#E12885",
            Swatches = ["#86CECB", "#137A7F", "#BEC8D1", "#E12885", "#5B8FA8", "#7DE8DD", "#9FB4BD", "#4FC1E9"],
            GroupBgs = ["#4FC1E9", "#7DE8DD", "#E12885", "#F5D76E"],
            LogoUri = "avares://SONO.App/Assets/logo-miku.png",
        },
    ];

    public static Palette Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? Default;
}
