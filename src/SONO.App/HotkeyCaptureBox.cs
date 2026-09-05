using System.Drawing.Drawing2D;

namespace SONO.App;

/// <summary>
/// Shortcut capture field — a fully owner-drawn control (deliberately NOT a TextBox:
/// native EDIT painting broke with region-clipped ancestors). Click to capture; the next
/// key combo (or bare multimedia key) becomes the binding. Esc cancels, ✕-button clears.
/// </summary>
public class HotkeyCaptureBox : Control
{
    private static readonly HashSet<string> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
        { "Control", "ControlKey", "Menu", "Alt", "Shift", "ShiftKey", "LWin", "RWin", "LControlKey", "RControlKey", "LMenu", "RMenu", "LShiftKey", "RShiftKey" };

    private static readonly HashSet<Keys> MediaKeys = new()
    {
        Keys.VolumeUp, Keys.VolumeDown, Keys.VolumeMute,
        Keys.MediaNextTrack, Keys.MediaPreviousTrack, Keys.MediaStop, Keys.MediaPlayPause,
    };

    private static readonly Dictionary<Keys, string> MediaNames = new()
    {
        [Keys.VolumeUp] = "VolumeUp",
        [Keys.VolumeDown] = "VolumeDown",
        [Keys.VolumeMute] = "VolumeMute",
        [Keys.MediaNextTrack] = "MediaNext",
        [Keys.MediaPreviousTrack] = "MediaPrev",
        [Keys.MediaStop] = "MediaStop",
        [Keys.MediaPlayPause] = "MediaPlay",
    };

    public bool Capturing { get; private set; }

    /// <summary>Raised with the captured combo text, or null when cleared with Esc.</summary>
    public event Action<string?>? Committed;

    public HotkeyCaptureBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string HotkeyText
    {
        get => Tag as string ?? "(none)";
        set { Tag = value; Invalidate(); }
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Capturing = true;
        Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Capturing = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
    }

    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        // keep arrow/tab keys for capture instead of focus navigation
        if (Capturing) e.IsInputKey = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Capturing) return;
        e.Handled = true;

        if (e.KeyCode == Keys.Escape) { SetHotkey(null); return; }

        var key = e.KeyCode;
        if (ModifierNames.Contains(key.ToString())) return;   // wait for a non-modifier

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        if (e.Modifiers.HasFlag(Keys.LWin) || e.Modifiers.HasFlag(Keys.RWin)) parts.Add("Win");
        if (parts.Count == 0 && !MediaKeys.Contains(key)) return;   // bare key: only media allowed
        parts.Add(FormatKey(key));
        SetHotkey(string.Join("+", parts));
    }

    protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }   // no beep

    private void SetHotkey(string? combo)
    {
        HotkeyText = combo ?? "(none)";
        Committed?.Invoke(combo);
        Invalidate();
    }

    private static string FormatKey(Keys key)
    {
        if (MediaNames.TryGetValue(key, out var mediaName)) return mediaName;
        var s = key.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1..];   // D1 → "1"
        if (s.Length == 1 && char.IsLetter(s[0])) return s.ToUpperInvariant();
        return s;   // "F1", "Space", …
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var bg = new SolidBrush(Capturing ? Theme.FieldFocus : Theme.Field);
        g.Clear(BackColor);
        g.FillPath(bg, SliderBar.RoundRect(0, 0, Width, Height, Height / 2));

        string text = Capturing ? "Press a combo…" : HotkeyText;
        var color = Capturing ? Theme.Accent : (Text == "(none)" || HotkeyText == "(none)" ? Theme.Muted : Theme.Text);
        TextRenderer.DrawText(g, text, Font, ClientRectangle, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);
        if (Capturing)
        {
            // simple focus ring (material)
            using var pen = new Pen(Theme.Accent, 1.6f);
            g.DrawPath(pen, SliderBar.RoundRect(1, 1, Width - 3, Height - 3, Height / 2 - 1));
        }
    }

    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Invalidate(); }
}
