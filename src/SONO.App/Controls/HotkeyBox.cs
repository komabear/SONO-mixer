using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SONO.App.Themes;

namespace SONO.App.Controls;

/// <summary>
/// Click-to-arm hotkey capture field. Armed: global hotkeys are suspended (so the keys
/// reach us), keydowns are swallowed until a valid combo (modifier+key, or a bare media
/// key) or Escape, then committed via event and global hotkeys resumed.
/// Right-click clears the binding.
/// </summary>
public sealed class HotkeyBox : Border
{
    public static readonly StyledProperty<string?> HotkeyTextProperty =
        AvaloniaProperty.Register<HotkeyBox, string?>(nameof(HotkeyText));

    public string? HotkeyText
    {
        get => GetValue(HotkeyTextProperty);
        set => SetValue(HotkeyTextProperty, value);
    }

    /// <summary>Commit callback (null = cleared). Receives the formatted combo text.</summary>
    public event Action<string?>? Committed;

    private bool _armed;
    private bool _rightDown;
    private readonly TextBlock _text;
    private readonly Grid _content = new();

    public HotkeyBox()
    {
        Focusable = true;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(6, 4);
        MinWidth = 0;
        MinHeight = 28;
        ClipToBounds = true;   // never paint outside our rounded box
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        Cursor = new Cursor(StandardCursorType.Hand);
        _text = new TextBlock { FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // ✕ clear affordance, visible only when a binding exists
        var clear = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        clear.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (_armed) return;
            HotkeyText = null;
            UpdateVisual();
            Committed?.Invoke(null);
        };
        // star column gives the text a REAL width constraint (StackPanel gave it infinity → no trim)
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_text, 0);
        Grid.SetColumn(clear, 1);
        _content.Children.Add(_text);
        _content.Children.Add(clear);
        Child = _content;
        UpdateVisual();
    }

    private static IBrush? BrushOf(string? hex) =>
        hex is not null && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : null;

    private static IBrush FallbackBrush => new SolidColorBrush(Color.Parse("#2C3046"));

    /// <summary>Optional group-tinted resting background; falls back to theme field color.</summary>
    public IBrush? BackgroundOverride { get; set; }

    private void UpdateVisual()
    {
        bool hasBinding = !string.IsNullOrWhiteSpace(HotkeyText);
        // group-derived fills: darker tint = empty, lighter tint = waiting
        var emptyFill = BackgroundOverride ?? BrushOf(ThemeManager.Current.Field) ?? FallbackBrush;
        var waitFill = Darker(BackgroundOverride as ImmutableSolidColorBrush, 0.25)
                       ?? BrushOf(ThemeManager.Current.FieldFocus) ?? FallbackBrush;
        Background = _armed ? waitFill : hasBinding ? BackgroundOverride ?? emptyFill
            : Darker(BackgroundOverride as ImmutableSolidColorBrush, 0.18) ?? emptyFill;
        _text.Text = _armed ? "Waiting for input…"
            : !hasBinding ? "None" : HotkeyText;
        _text.Foreground = _armed ? BrushOf(ThemeManager.Current.Text) ?? FallbackBrush
            : !hasBinding ? BrushOf(ThemeManager.Current.Muted) ?? FallbackBrush
            : BrushOf(ThemeManager.Current.Text) ?? FallbackBrush;
        // ✕ only when a binding exists and we're not capturing
        if (_content.Children.Count > 1)
            _content.Children[1].IsVisible = hasBinding && !_armed;
    }

    private static IBrush? Darker(ImmutableSolidColorBrush? b, double ratio)
    {
        if (b is null) return null;
        var c = b.Color;
        return new ImmutableSolidColorBrush(Color.FromArgb(c.A,
            (byte)(c.R * (1 - ratio)), (byte)(c.G * (1 - ratio)), (byte)(c.B * (1 - ratio))));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HotkeyTextProperty) UpdateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var right = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        _rightDown = right;
        if (right) return; // clear path handled in released
        if (!_armed)
        {
            _armed = true;
            App.Hotkeys?.Suspend();
            UpdateVisual();
            Focus();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_armed) { base.OnKeyDown(e); return; }
        e.Handled = true;

        if (e.Key == Key.Escape) { Disarm(hotkeyText: null); return; }

        // modifier alone: wait for the main key
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var key = e.Key;
        bool media = key is Key.VolumeUp or Key.VolumeDown or Key.VolumeMute;

        string name = key switch
        {
            Key.VolumeUp => "VolumeUp", Key.VolumeDown => "VolumeDown", Key.VolumeMute => "VolumeMute",
            Key.OemPlus => "OemPlus", Key.OemMinus => "OemMinus",
            Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown",
            Key.OemOpenBrackets => "OemOpenBrackets", Key.OemCloseBrackets => "OemCloseBrackets",
            Key.OemSemicolon => "OemSemicolon", Key.OemQuotes => "OemQuotes",
            Key.OemComma => "OemComma", Key.OemPeriod => "OemPeriod",
            Key.OemQuestion => "OemQuestion", Key.OemPipe => "OemPipe",
            _ => key.ToString(),
        };
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];   // D7 → 7
        if (name.StartsWith("NumPad")) name = "Num" + name["NumPad".Length..];

        var mods = e.KeyModifiers;
        if (mods == KeyModifiers.None && !media) return;   // bare non-media: keep waiting

        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(name);
        var text = string.Join("+", parts);

        HotkeyText = text;
        Disarm(text);
    }

    /// <summary>Escape = commit nothing but always resume hotkeys; a commit re-applies fresh bindings.</summary>
    private void Disarm(string? hotkeyText)
    {
        _armed = false;
        App.Hotkeys?.Resume();
        UpdateVisual();
        if (hotkeyText is not null || HotkeyText is null)
            Committed?.Invoke(hotkeyText);
        else
            Committed?.Invoke(null); // Escape with no prior binding
    }

    /// <summary>Right-click clears the binding.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var wasRight = _rightDown;
        _rightDown = false;
        if (!_armed && wasRight)
        {
            HotkeyText = null;
            UpdateVisual();
            Committed?.Invoke(null);
        }
    }
}
