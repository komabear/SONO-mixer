namespace SONO.App;

/// <summary>Shared dark renderer for every dropdown/menu surface (settings, gear, output picker, tray).</summary>
internal static class MenuTheme
{
    public sealed class SonoMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemSelected => Theme.FieldFocus;
        public override Color MenuItemSelectedGradientBegin => Theme.FieldFocus;
        public override Color MenuItemSelectedGradientEnd => Theme.FieldFocus;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }

    public static readonly ToolStripProfessionalRenderer Renderer = new(new SonoMenuColors());

    /// <summary>Apply the dark material look to any dropdown/menu.</summary>
    public static void Stylize(ToolStripDropDown menu)
    {
        menu.Renderer = Renderer;
        menu.BackColor = Theme.Card;
    }
}
