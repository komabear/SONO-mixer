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

/// <summary>Vertical fader (DAW-style): thin rounded track, colored fill from the bottom,
/// round thumb that ALWAYS stays inside the track (even at 0% and 100%). Pointer drag
/// with live Value updates; CommitRequested fires on release, LiveRequested while dragging.</summary>
public sealed class Fader : TemplatedControl
{
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Fader, double>(nameof(Minimum), 0);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Fader, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Fader, double>(
        nameof(Value), defaultValue: 100, coerce: CoerceValue);
    public static readonly StyledProperty<string?> GroupColorProperty = AvaloniaProperty.Register<Fader, string?>(nameof(GroupColor));

    private Canvas? _canvas;
    private Border? _fill;
    private Border? _thumb;
    private bool _dragging;
    private bool _suppress;

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
        AffectsArrange<Fader>(ValueProperty, MinimumProperty, MaximumProperty);
        ValueProperty.Changed.AddClassHandler<Fader>((f, _) => f.UpdateThumb());
        GroupColorProperty.Changed.AddClassHandler<Fader>((f, _) => f.UpdateColors());
    }

    public Fader()
    {
        // build visuals in the constructor — a TemplatedControl without a XAML template
        // never gets OnApplyTemplate, so template-based construction silently draws nothing
        _canvas = new Canvas { ClipToBounds = true };
        _fill = new Border { CornerRadius = new CornerRadius(9), IsHitTestVisible = false };
        _thumb = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(2.5),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 6, Color = Color.FromArgb(120, 0, 0, 0), OffsetY = 2 }),
        };
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_thumb);
        VisualChildren.Add(_canvas);
        LogicalChildren.Add(_canvas);
        UpdateColors();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateColors();
        PositionThumb();
    }

    private void UpdateColors()
    {
        if (_fill is null || _thumb is null) return;
        if (Color.TryParse(GroupColor, out var c))
        {
            var col = c;
            // fill: vertical gradient, strong at bottom → lighter at the surface
            _fill.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, col.R, col.G, col.B), 0),
                    new GradientStop(Color.FromArgb(235, col.R, col.G, col.B), 1),
                },
            };
            _thumb.Background = new ImmutableSolidColorBrush(Color.FromArgb(255, 43, 45, 53));
            _thumb.BorderBrush = new ImmutableSolidColorBrush(col);
        }
        else
        {
            _fill.Background = Brushes.Gray;
            _thumb.Background = Brushes.DimGray;
            _thumb.BorderBrush = Brushes.Gray;
        }
    }

    private void UpdateThumb()
    {
        if (_thumb is not null && !_dragging) PositionThumb();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        PositionThumb();
        return base.ArrangeOverride(finalSize);
    }

    private void PositionThumb()
    {
        if (_canvas is null || _fill is null || _thumb is null || _canvas.Bounds.Height < 1) return;
        double h = _canvas.Bounds.Height;
        double w = _canvas.Bounds.Width;
        double trackW = 18;
        double x = (w - trackW) / 2;

        double range = Maximum - Minimum;
        double frac = range <= 0 ? 1 : Math.Clamp((Value - Minimum) / range, 0, 1);
        double fillH = frac * h;

        // thumb rides the TOP of the fill (fill grows from bottom); clamp so the round
        // thumb never leaves the track at 0% or 100%
        double minCy = 11;              // half thumb height
        double maxCy = h - 11;
        double cy = Math.Clamp(h - fillH, minCy, maxCy);

        Canvas.SetLeft(_fill, x);
        Canvas.SetTop(_fill, h - fillH);
        _fill.Width = trackW;
        _fill.Height = fillH;

        Canvas.SetLeft(_thumb, (w - _thumb.Width) / 2);
        Canvas.SetTop(_thumb, cy - 11);
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
        var p = e.GetPosition(_canvas);
        double h = _canvas.Bounds.Height;
        if (h < 1) return;
        double frac = Math.Clamp(1 - p.Y / h, 0, 1);
        _suppress = false;
        Value = Minimum + frac * (Maximum - Minimum);
        PositionThumb();
        ValueChanged?.Invoke();
        LiveRequested?.Invoke();
    }
}
