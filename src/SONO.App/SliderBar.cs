using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace SONO.App;

/// <summary>Flat horizontal volume slider, custom-painted (no WinForms TrackBar chrome).</summary>
public class SliderBar : Control
{
    private const int PadX = 10, TrackH = 8, ThumbR = 9;
    private float _val = 1f;
    private bool _hover, _drag;
    private Color _fill = Theme.Accent;

    /// <summary>Live while the user drags.</summary>
    public event Action<float>? ValueChanged;

    /// <summary>Once, on release.</summary>
    public event Action<float>? EditCommitted;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get => _fill; set { _fill = value; Invalidate(); } }
    public float Value => _val;
    public bool IsDragging => _drag;

    private string? _toolTip;
    private static readonly ToolTip Tips = new();
    private bool _readOnly;
    /// <summary>Informative-only mode: displays a value but ignores mouse input.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _readOnly;
        set { _readOnly = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); }
    }
    /// <summary>Static tooltip text. The live dB value is appended while dragging.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? ToolTip
    {
        get => _toolTip;
        set { _toolTip = value; if (value is not null && Parent is not null) Tips.SetToolTip(this, value); }
    }

    private void ShowDbTip()
    {
        if (Parent is null) return;
        string db = _val <= 0.0005f ? "−∞ dB" : $"{20 * Math.Log10(_val),+0:0.0} dB";
        Tips.SetToolTip(this, $"{_toolTip ?? "Volume"}: {db}");
    }

    public SliderBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    /// <summary>Sync from outside (engine tick) — ignored mid-drag so we never fight the user.</summary>
    public void SetValueExternal(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        if (Math.Abs(v - _val) > 0.001f) { _val = v; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        int x0 = PadX, x1 = Math.Max(x0 + 1, Width - PadX);
        int fw = (int)((x1 - x0) * _val);

        using var track = RoundRect(x0, cy - TrackH / 2, x1 - x0, TrackH, TrackH / 2);
        using (var b = new SolidBrush(Theme.Chip)) g.FillPath(b, track);

        if (fw > TrackH)
        {
            using var fill = RoundRect(x0, cy - TrackH / 2, fw, TrackH, TrackH / 2);
            using var fb = new SolidBrush(_fill);
            g.FillPath(fb, fill);
        }
        else if (fw > 0)
        {
            using var fb = new SolidBrush(_fill);
            g.FillRectangle(fb, x0, cy - TrackH / 2, fw, TrackH);
        }

        // read-only sliders (Applications panel readouts) omit the thumb: a ball invites
        // dragging. Just the filled track.
        if (_readOnly) return;

        int r = ThumbR + (_hover || _drag ? 1 : 0);
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, x0 + fw - r, cy - r, r * 2, r * 2);
        using (var pen = new Pen(_fill, 2f))
            g.DrawEllipse(pen, x0 + fw - r, cy - r, r * 2, r * 2);
    }

    internal static GraphicsPath RoundRect(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        if (w <= 0 || h <= 0) { p.AddRectangle(new Rectangle(x, y, 1, 1)); return p; }
        r = Math.Min(r, Math.Min(w, h) / 2);
        if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; }
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void SetFromX(int x)
    {
        float v = Math.Clamp((x - PadX) / (float)Math.Max(1, Width - PadX * 2), 0f, 1f);
        if (Math.Abs(v - _val) > 0.0005f)
        {
            _val = v;
            Invalidate();
            ValueChanged?.Invoke(v);
            ShowDbTip();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (_readOnly) return; _drag = true; Capture = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_readOnly || !_drag) return; SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag) { _drag = false; Invalidate(); EditCommitted?.Invoke(_val); }
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
}
