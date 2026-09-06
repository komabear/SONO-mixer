using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>Sonar-style volume OSD: a tiny popup on hotkey presses showing which channel's
/// volume changed. Never takes focus (WS_EX_NOACTIVATE), click-through (WS_EX_TRANSPARENT),
/// no taskbar/Alt-Tab (WS_EX_TOOLWINDOW), always on top. Visible ~1.2 s after each press,
/// hidden otherwise — idle cost is zero (no timers, no painting).</summary>
public sealed class OsdWindow : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
    private const uint LWA_ALPHA = 2;

    private ChannelDefinition? _def;
    private string _valueText = "0%";
    private readonly System.Windows.Forms.Timer _hide = new();

    /// <summary>Returns the current anchor setting ("off"/"top-left"/…/"bottom-right").</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string>? AnchorProvider { get; set; }

    public OsdWindow()
    {
        var theme = Theme.Current;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        ControlBox = false;
        BackColor = theme.Card;
        ClientSize = new Size(264, 68);
        DoubleBuffered = true;

        _hide.Interval = 1200;
        _hide.Tick += (_, _) => Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000008      // WS_EX_TOPMOST
                        | 0x00000080      // WS_EX_TOOLWINDOW (no taskbar/alt-tab)
                        | 0x08000000      // WS_EX_NOACTIVATE (never steals focus)
                        | 0x00080000      // WS_EX_LAYERED
                        | 0x00000020;     // WS_EX_TRANSPARENT (click-through)
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // layered + normal painting: full opacity via SetLayeredWindowAttributes.
        // NOTE: no Region clipping — a per-pixel-alpha layered window with a Region
        // clips GDI painting unpredictably (the volume bar was being cut off).
        // The rounded look is painted directly in OnPaint instead.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
    }

    /// <summary>Show/update the OSD for a channel's current volume/mute state.</summary>
    public void Notify(ChannelDefinition def)
    {
        _def = def;
        _valueText = def.Muted ? "MUTED" : $"{MathF.Round(def.Volume * 100f):0}%";
        PositionAtAnchor();
        if (!Visible) Show();          // ShowWithoutActivation → no focus steal
        Invalidate();
        _hide.Stop();
        _hide.Start();
    }

    public new void Hide()
    {
        _hide.Stop();
        base.Hide();
    }

    private void PositionAtAnchor()
    {
        string anchor = AnchorProvider?.Invoke() ?? "bottom-right";
        if (anchor == "off") return;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        const int mx = 16, my = 16;
        int w = ClientSize.Width, h = ClientSize.Height;
        int x, y;

        switch (anchor)
        {
            case "top-left":     x = wa.Left + mx;                y = wa.Top + my; break;
            case "top":          x = wa.Left + (wa.Width - w) / 2; y = wa.Top + my; break;
            case "top-right":    x = wa.Right - w - mx;           y = wa.Top + my; break;
            case "left":         x = wa.Left + mx;                y = wa.Top + (wa.Height - h) / 2; break;
            case "center":       x = wa.Left + (wa.Width - w) / 2; y = wa.Top + (wa.Height - h) / 2; break;
            case "right":        x = wa.Right - w - mx;           y = wa.Top + (wa.Height - h) / 2; break;
            case "bottom-left":  x = wa.Left + mx;                y = wa.Bottom - h - my; break;
            case "bottom":       x = wa.Left + (wa.Width - w) / 2; y = wa.Bottom - h - my; break;
            default:             x = wa.Right - w - mx;           y = wa.Bottom - h - my; break; // bottom-right
        }
        Location = new Point(x, y);
    }

    /// <summary>Re-position for the current anchor (called on creation; cheap, no paint).</summary>
    public void PositionAtAnchorPublic() => PositionAtAnchor();

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_def is null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var theme = Theme.Current;
        var accent = ColorTranslator.FromHtml(_def.ColorHex);

        // rounded dark card (paint it — the Region clip uses the same shape)
        using var card = new SolidBrush(theme.Card);
        g.FillPath(card, RoundedPath(0, 0, ClientSize.Width - 1, ClientSize.Height - 1, 12));
        using var border = new Pen(theme.Border);
        g.DrawPath(border, RoundedPath(0, 0, ClientSize.Width - 1, ClientSize.Height - 1, 12));

        // channel name (top-left) and value (top-right)
        using var nameFont = new Font("Segoe UI Semibold", 10f);
        using var valFont = new Font("Segoe UI", 9f);
        TextRenderer.DrawText(g, _def.Name, nameFont, new Point(14, 8), theme.Text);
        var valSize = TextRenderer.MeasureText(g, _valueText, valFont);
        TextRenderer.DrawText(g, _valueText, valFont,
            new Point(ClientSize.Width - valSize.Width - 14, 10), _def.Muted ? theme.Danger : accent);

        // slim volume bar, channel-colored
        int bx = 14, by = 42, bw = ClientSize.Width - 28, bh = 10;
        var track = RoundedPath(bx, by, bw, bh, bh / 2f);
        using (var tb = new SolidBrush(theme.Field)) g.FillPath(tb, track);
        float frac = Math.Clamp(_def.Muted ? 0f : _def.Volume, 0f, 1f);
        if (frac > 0.003f)
        {
            int fw = Math.Max(bh, (int)(bw * frac));
            using var fb = new SolidBrush(_def.Muted ? theme.Muted : accent);
            g.FillPath(fb, RoundedPath(bx, by, fw, bh, bh / 2f));
        }
    }

    private static GraphicsPath RoundedPath(int x, int y, int w, int h, float r)
    {
        var p = new GraphicsPath();
        if (w <= 0 || h <= 0) { p.AddRectangle(new Rectangle(x, y, 1, 1)); return p; }
        if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; }
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Region RoundedRegion(int w, int h, float r)
        => new(RoundedPath(0, 0, w, h, r));
}
