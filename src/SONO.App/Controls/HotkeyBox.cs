using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    public HotkeyBox()
    {
        Focusable = true;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(6, 4);
        MinWidth = 0;
        MinHeight = 28;
        Cursor = new Cursor(StandardCursorType.Hand);
        _text = new TextBlock { FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Child = _text;
        UpdateVisual();
    }

    private static IBrush? BrushOf(string? hex) =>
        hex is not null && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : null;

    private static IBrush FallbackBrush => new SolidColorBrush(Color.Parse("#2C3046"));

    private void UpdateVisual()
    {
        var p = ThemeManager.Current;
        Background = _armed ? BrushOf(p.FieldFocus) ?? FallbackBrush : BrushOf(p.Field) ?? FallbackBrush;
        _text.Text = _armed ? "press keys…"
            : string.IsNullOrWhiteSpace(HotkeyText) ? "click to bind" : HotkeyText;
        _text.Foreground = _armed ? BrushOf(p.Accent) ?? FallbackBrush
            : string.IsNullOrWhiteSpace(HotkeyText) ? BrushOf(p.Muted) ?? FallbackBrush
            : BrushOf(p.Text) ?? FallbackBrush;
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
