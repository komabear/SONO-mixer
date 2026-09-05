using System.Runtime.InteropServices;

namespace SONO.App;

/// <summary>
/// Flow list with stylized scrolling: native scrollbars suppressed (horizontal removed
/// outright, vertical hidden), wheel still scrolls, and a slim rounded thumb is drawn
/// on the right edge — draggable.
/// </summary>
public class SmoothFlowPanel : BufferedFlow
{
    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

    private const int SB_HORZ = 0, SB_VERT = 1;
    private const int WS_HSCROLL = 0x00100000;

    private bool _draggingThumb;
    private int _dragStartY, _dragStartVal;

    public event Action? ScrollChanged;

    public SmoothFlowPanel()
    {
        DoubleBuffered = true;
        AutoScroll = true;
    }

    /// <summary>Strip WS_HSCROLL so the horizontal scrollbar can never exist.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style &= ~WS_HSCROLL;
            return cp;
        }
    }

    private void HideBars()
    {
        if (!IsHandleCreated) return;
        try
        {
            ShowScrollBar(Handle, SB_HORZ, false);
            ShowScrollBar(Handle, SB_VERT, false);
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); HideBars(); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        HideBars();
        Invalidate();
        ScrollChanged?.Invoke();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); HideBars(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        HideBars();
        Invalidate();
        ScrollChanged?.Invoke();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        HideBars();
        Invalidate();
        ScrollChanged?.Invoke();
    }

    // ---- custom slim thumb ----

    private bool HasOverflow()
    {
        int content = Math.Max(DisplayRectangle.Height, AutoScrollMinSize.Height);
        return content > ClientSize.Height + 2;
    }

    private (int Track, int ThumbH, int ThumbY, int Max) ThumbGeometry()
    {
        int view = ClientSize.Height;
        int content = Math.Max(DisplayRectangle.Height, AutoScrollMinSize.Height);
        int max = Math.Max(1, content - view);
        int thumbH = Math.Max(44, (int)((long)view * view / Math.Max(1, content)));
        int val = Math.Clamp(-AutoScrollPosition.Y, 0, max);
        int thumbY = (int)((long)val * (view - thumbH) / max);
        return (view, thumbH, thumbY, max);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!HasOverflow()) return;
        var (_, thumbH, thumbY, _) = ThumbGeometry();
        int x = Width - 8;
        using var b = new SolidBrush(Color.FromArgb(_draggingThumb ? 150 : 90, Theme.Text));
        using var path = SliderBar.RoundRect(x, thumbY + 2, 5, thumbH - 4, 2);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !HasOverflow() || e.X < Width - 12) return;
        var (_, thumbH, thumbY, _) = ThumbGeometry();
        if (e.Y < thumbY || e.Y > thumbY + thumbH) return;   // page-jump not needed
        _draggingThumb = true;
        _dragStartY = e.Y;
        _dragStartVal = Math.Clamp(-AutoScrollPosition.Y, 0, ThumbGeometry().Max);
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_draggingThumb) return;
        var (view, thumbH, _, max) = ThumbGeometry();
        int delta = (int)((long)(e.Y - _dragStartY) * max / Math.Max(1, view - thumbH));
        int target = Math.Clamp(_dragStartVal + delta, 0, max);
        AutoScrollPosition = new Point(0, target);
        HideBars();
        Invalidate();
        ScrollChanged?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_draggingThumb) { _draggingThumb = false; Invalidate(); }
    }
}
