using System.Drawing.Drawing2D;

namespace SONO.App;

/// <summary>A small text box that captures a key combo into "Ctrl+Alt+X" text.</summary>
public class HotkeyCaptureBox : TextBox
{
    private static readonly HashSet<string> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
        { "Control", "ControlKey", "Menu", "Alt", "Shift", "ShiftKey", "LWin", "RWin", "LControlKey", "RControlKey", "LMenu", "RMenu", "LShiftKey", "RShiftKey" };

    public bool Capturing { get; private set; }

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        PlaceholderText = "click, then press keys";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused || !Capturing) return base.ProcessCmdKey(ref msg, keyData);
        return false;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Capturing = true;
        Text = "Press a combo…";
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Capturing = false;
        Text = Tag as string ?? "";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        if (e.KeyCode == Keys.Escape) { Commit(null); return; }

        var key = e.KeyCode;
        if (ModifierNames.Contains(key.ToString())) return; // wait for a non-modifier

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        if (e.Modifiers.HasFlag(Keys.LWin) || e.Modifiers.HasFlag(Keys.RWin)) parts.Add("Win");
        if (parts.Count == 0) return; // require a modifier
        parts.Add(FormatKey(key));
        Commit(string.Join("+", parts));
    }

    private static string FormatKey(Keys key)
    {
        var s = key.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1..]; // Keys.D1 → "1"
        if (s.Length == 1 && char.IsLetter(s[0])) return s.ToUpperInvariant();
        return s; // "F1", "Space", "Insert", … — HotkeyManager understands these
    }

    /// <summary>Raised with the captured combo text, or null when cleared with Esc.</summary>
    public event Action<string?>? Committed;

    private void Commit(string? combo)
    {
        Tag = combo;
        Text = combo ?? "(none)";
        if (combo is not null) Parent?.Focus(); // release focus so the next click re-arms another box
        Committed?.Invoke(combo);
    }
}
