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
    private Avalonia.Controls.Shapes.Path? _wave;
    private DispatcherTimer? _waveTimer;
    private double _phase;
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
        _fill = new Border { CornerRadius = new CornerRadius(22), IsHitTestVisible = false };
        _wave = new Avalonia.Controls.Shapes.Path
        {
            IsHitTestVisible = false,
            Opacity = 0.9,
        };
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_wave);
        UpdateColors();

        // wave animation: only ticks when there's sound to show
        _waveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _waveTimer.Tick += (_, _) => { _phase += 0.35; InvalidateArrange(); };
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

        // water surface: sine wave across the fill's top edge; amplitude scales with Level,
        // phase animates so it sloshes. Skipped entirely when idle (no timer, no cost).
        double level = Math.Clamp(Level, 0, 1);
        bool timerShouldRun = level > 0.02 && fillH > 20 && _track is not null;
        if (timerShouldRun && _waveTimer is not null && !_waveTimer.IsEnabled) _waveTimer.Start();
        if (!timerShouldRun && _waveTimer is not null && _waveTimer.IsEnabled) _waveTimer.Stop();

        if (_wave is not null)
        {
            if (!timerShouldRun)
            {
                _wave.Data = null;
            }
            else
            {
                double amp = 2.0 + Math.Sqrt(level) * 9.0;   // sqrt: quiet audio still visible
                double surfaceY = h - fillH;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(x, surfaceY - amp), true);
                    int steps = 14;
                    for (int i = 1; i <= steps; i++)
                    {
                        double t = (double)i / steps;
                        double wx = x + trackW * t;
                        double wy = surfaceY - amp + Math.Sin(t * Math.PI * 2.4 + _phase) * amp;
                        ctx.LineTo(new Point(wx, wy));
                    }
                    ctx.LineTo(new Point(x + trackW, surfaceY + 4));
                    ctx.LineTo(new Point(x, surfaceY + 4));
                    ctx.EndFigure(true);
                }
                _wave.Data = geo;
                ArrangeChild(_wave, 0, 0, w, h);
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
