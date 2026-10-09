using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace SONO.App.Controls;

/// <summary>Vertical fader (DAW-style): rounded track, colored fill from the bottom,
/// round thumb that ALWAYS stays inside the track (even at 0% and 100%).
/// Built as a Panel subclass so the layout system measures/arranges the children
/// natively — the earlier TemplatedControl + manually-attached-Canvas approach had
/// stale arrange passes (a fader could render taller than its siblings after a click).
/// Pointer drag with live Value updates; CommitRequested fires on release,
/// LiveRequested while dragging.</summary>
public sealed class Fader : Panel
{
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Fader, double>(nameof(Minimum), 0);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Fader, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Fader, double>(
        nameof(Value), defaultValue: 100, coerce: CoerceValue);
    public static readonly StyledProperty<string?> GroupColorProperty = AvaloniaProperty.Register<Fader, string?>(nameof(GroupColor));

    private Border? _track;
    private Border? _fill;
    private Border? _meter;
    private double _lastCoreH;
    private double _smoothed;
    private bool _dragging;

    /// <summary>Live output level 0..1 for this channel (engine peak). Drives wave amplitude.</summary>
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<Fader, double>(nameof(Level), 0);
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    public event Action? ValueChanged;
    public event Action? CommitRequested;
    public event Action? LiveRequested;

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? GroupColor { get => GetValue(GroupColorProperty); set => SetValue(GroupColorProperty, value); }

    private static double CoerceValue(AvaloniaObject s, double v)
    {
        var f = (Fader)s;
        return Math.Clamp(v, f.Minimum, f.Maximum);
    }

    static Fader()
    {
        AffectsArrange<Fader>(ValueProperty, MinimumProperty, MaximumProperty, LevelProperty);
        ValueProperty.Changed.AddClassHandler<Fader>((f, _) => { if (!f._dragging) f.InvalidateArrange(); });
        GroupColorProperty.Changed.AddClassHandler<Fader>((f, _) => f.UpdateColors());
    }

    public Fader()
    {
        // transparent background = hit-testable (null background never receives PointerPressed)
        Background = Brushes.Transparent;
        ClipToBounds = true;

        // structure: TRACK (pill clip) ──> FILL (clipped child) ──> CORE
        // the track's rounded corners clip everything inside at EVERY height — no radius
        // juggling at low volume
        _fill = new Border
        {
            IsHitTestVisible = false,
            ClipToBounds = true,          // rounds the core inside the fill
            CornerRadius = new CornerRadius(17),   // pill inner curve; dynamic below
        };
        _meter = new Border
        {
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(3, 3, 3, 3),
        };
        _fill.Child = _meter;
        _track = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = new ImmutableSolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            ClipToBounds = true,          // rounds all children: fill + core
            Child = _fill,                // attach AFTER _fill exists (ctor ordering!)
        };
        Children.Add(_track);
        UpdateColors();
    }

    private void UpdateColors()
    {
        if (_fill is null) return;
        if (Color.TryParse(GroupColor, out var c))
        {
            _fill.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(235, c.R, c.G, c.B), 1),
                },
            };
            if (_meter is not null)
                _meter.Background = new ImmutableSolidColorBrush(Color.FromArgb(
                    235,
                    (byte)Math.Min(255, c.R + 50),
                    (byte)Math.Min(255, c.G + 50),
                    (byte)Math.Min(255, c.B + 50)));
        }
        else
        {
            _fill.Background = Brushes.Gray;
            if (_meter is not null) _meter.Background = Brushes.Gray;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // children are positioned manually; they need no intrinsic size
        foreach (var child in Children) child.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double h = finalSize.Height;
        double w = finalSize.Width;
        if (h < 1 || _track is null || _fill is null) return finalSize;

        double trackW = 76;
        double x = Math.Max(0, (w - trackW) / 2);

        double range = Maximum - Minimum;
        double frac = range <= 0 ? 1 : Math.Clamp((Value - Minimum) / range, 0, 1);
        double fillH = frac * h;

        ArrangeChild(_track, x, 0, trackW, h);
        // zero-volume: hide the fill + core outright (visibility, not just 0-size arrange —
        // a same-frame 0-size arrange can render one stale frame before taking effect)
        if (fillH < 1)
        {
            // arrange to EMPTY rects (never IsVisible=false: a freshly-visible control
            // renders once with its stale last-arranged size — the "100% flash")
            ArrangeChild(_fill, 0, h, trackW, 0);
            ArrangeChild(_meter, 0, 0, 0, 0);
            return finalSize;
        }
        double fillR = Math.Min(17, fillH / 2);
        _fill.CornerRadius = new CornerRadius(fillR);
        _fill.ClipToBounds = true;   // clip the core to the fill's (dynamic) rounding
        ArrangeChild(_fill, 0, h - fillH, trackW, fillH);   // coords relative to the TRACK

        // LIVE CORE: an inner brighter bar inside the fill, bottom-anchored.
        // SENSITIVITY: steep gamma on the RAW level — Windows session peaks for music sit
        // 0.4..1.0, and ^2.5 spreads them across the bar: quiet parts ~0.1, average ~0.4,
        // hits 1.0.
        // LERP: exponential chase toward the live level (45% of the remaining gap per
        // frame) — soft motion, no snap, symmetric rise/fall. Safe now that the
        // stale-size flash is fixed at the root (empty-rect hiding).
        double target = Math.Pow(Math.Clamp(Level, 0, 1), 2.5);
        _smoothed += (target - _smoothed) * 0.45;
        if (Math.Abs(target - _smoothed) < 0.001) _smoothed = target;   // settle exactly
        double level = _smoothed;
        // the lerp must CONTINUE after the last engine push (song stopped → Level stays 0,
        // no more PropertyChanged → no arranges → the bar would freeze mid-fall)
        EnsureFallTimer();

        if (_meter is not null)
        {
            // whole-pixel height: sub-pixel sizes made the bottom edge wobble ±1px
            double coreH = Math.Floor(Math.Min(fillH, fillH * level));
            if (Math.Abs(coreH - _lastCoreH) > fillH * 0.15 || (_lastCoreH <= 2) != (coreH <= 2))
                SONO.Core.Diagnostics.Log.Write($"eq: raw={Level:0.000} shaped={level:0.000} coreH={coreH:0} fillH={fillH:0} last={_lastCoreH:0}");
            _lastCoreH = coreH;
            if (coreH > 2)
            {
                // same width as the fill — bottom flush, clipped by the same rounding
                ArrangeChild(_meter, 0, fillH - coreH, trackW, coreH);
                double r = Math.Min(17, coreH / 2);
                _meter.CornerRadius = new CornerRadius(r, r, r, r);
                _meter.IsVisible = true;
            }
            else
            {
                ArrangeChild(_meter, 0, 0, 0, 0);   // empty rect: renders nothing, no stale size
            }
        }
        EnsureFallTimer();

        return finalSize;
    }

    private DispatcherTimer? _fallTimer;

    /// <summary>33ms ticker that keeps the lerp animating between engine pushes; stops
    /// itself once the bar has fully settled (zero cost at rest).</summary>
    private void EnsureFallTimer()
    {
        if (_fallTimer is not null) return;
        _fallTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _fallTimer.Tick += (_, _) =>
        {
            double target = Math.Pow(Math.Clamp(Level, 0, 1), 2.5);
            _smoothed += (target - _smoothed) * 0.45;
            if (Math.Abs(target - _smoothed) < 0.002)
            {
                _smoothed = target;
                _fallTimer?.Stop();
                _fallTimer = null;
            }
            InvalidateArrange();
        };
        _fallTimer.Start();
    }

    private static void ArrangeChild(Control c, double x, double y, double w, double h)
    {
        if (w < 0) w = 0;
        if (h < 0) h = 0;
        c.Arrange(new Rect(x, y, w, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            SetFromPointer(e);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            SetFromPointer(e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            CommitRequested?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            CommitRequested?.Invoke();
        }
    }

    private void SetFromPointer(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        double h = Bounds.Height;
        if (h < 1) return;
        double frac = Math.Clamp(1 - p.Y / h, 0, 1);
        Value = Minimum + frac * (Maximum - Minimum);
        InvalidateArrange();
        ValueChanged?.Invoke();
        LiveRequested?.Invoke();
    }
}
