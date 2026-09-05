namespace SONO.App;

/// <summary>ComboBox drawn on tonal surfaces — no white list/box chrome (material dark).</summary>
public class DarkComboBox : ComboBox
{
    public DarkComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 22;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(selected ? Theme.FieldFocus : Theme.Field);
        e.Graphics.FillRectangle(bg, e.Bounds);
        using var tx = new SolidBrush(Theme.Text);
        var text = e.Index < Items.Count ? Items[e.Index]?.ToString() ?? "" : "";
        TextRenderer.DrawText(e.Graphics, text, Font, e.Bounds, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
