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
    private Border? _glow;
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

        _track = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = new ImmutableSolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            IsHitTestVisible = false,
        };
        // square top corners: the wave surface must join the fill flush (a domed top
        // leaves notches at the sides where the wave can't reach)
        _fill = new Border { CornerRadius = new CornerRadius(22), IsHitTestVisible = false };
        _glow = new Border
        {
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(4),
        };
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_glow);
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
            if (_glow is not null)
                _glow.Background = new ImmutableSolidColorBrush(Color.FromArgb(
                    255,
                    (byte)Math.Min(255, c.R + 70),
                    (byte)Math.Min(255, c.G + 70),
                    (byte)Math.Min(255, c.B + 70)));
        }
        else
        {
            _fill.Background = Brushes.Gray;
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
        ArrangeChild(_fill, x, h - fillH, trackW, fillH);

        // level glow: a soft light band at the fill's surface, brightness/thickness
        // following the live output level — the fill looks "hot" where sound comes out
        double level = Math.Clamp(Level, 0, 1);
        if (_glow is not null)
        {
            if (level > 0.02 && fillH > 10)
            {
                double glowH = 4 + level * 8;                 // 4–12 px tall
                double surfaceY = h - fillH;
                ArrangeChild(_glow, x, surfaceY - glowH / 2, trackW, glowH);
                _glow.IsVisible = true;
                _glow.Opacity = 0.35 + level * 0.5;           // 35–85%
            }
            else
            {
                _glow.IsVisible = false;
            }
        }

        return finalSize;
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
