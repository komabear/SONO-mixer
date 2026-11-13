using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
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
    private DispatcherTimer? _timeout;
    private static HotkeyBox? _armedBox;   // only one capture may be armed app-wide
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

    private string? _error;
    /// <summary>Set when the shown binding is NOT live (taken by another program, bound
    /// twice, unsupported key). The combination is then drawn in the danger color with the
    /// reason as its tooltip, instead of looking like a working hotkey.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            UpdateVisual();
        }
    }

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
        bool failed = hasBinding && !_armed && _error is not null;
        _text.Foreground = _armed ? BrushOf(ThemeManager.Current.Text) ?? FallbackBrush
            : !hasBinding ? BrushOf(ThemeManager.Current.Muted) ?? FallbackBrush
            : failed ? BrushOf(ThemeManager.Current.Danger) ?? FallbackBrush
            : BrushOf(ThemeManager.Current.Text) ?? FallbackBrush;
        ToolTip.SetTip(this, failed ? "Not active: " + _error : null);
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Themes.ThemeManager.ThemeChanged += OnThemeChanged;
        UpdateVisual();   // re-derive colors for the theme active at attach time
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Themes.ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>External theme refresh (parent strip calls this on its own repaint path).</summary>
    public void RefreshTheme() => UpdateVisual();

    private void OnThemeChanged()
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateVisual);
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write("hotkeybox restyle: " + ex.Message); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var right = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        _rightDown = right;
        if (right) return; // clear path handled in released
        if (!_armed)
        {
            if (_armedBox is not null && !ReferenceEquals(_armedBox, this))
                _armedBox.Disarm(hotkeyText: null);   // clicking another box cancels the first
            _armed = true;
            _armedBox = this;
            App.Hotkeys?.Suspend();
            UpdateVisual();
            Focus();
            // auto-cancel: captures must not linger forever (hotkeys stay suspended!)
            _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timeout.Tick += (_, _) => Disarm(hotkeyText: null);
            _timeout.Start();
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
        // consumer-control keys may be bound bare — the same set HotkeyManager.IsMediaVk accepts
        bool media = key is Key.VolumeUp or Key.VolumeDown or Key.VolumeMute
            or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop or Key.MediaPlayPause;

        // every name produced here must be one HotkeyManager.TryParseKey understands
        // (several Key values have two enum names, so the ambiguous ones are spelled out)
        string name = key switch
        {
            Key.VolumeUp => "VolumeUp", Key.VolumeDown => "VolumeDown", Key.VolumeMute => "VolumeMute",
            Key.MediaNextTrack => "MediaNext", Key.MediaPreviousTrack => "MediaPrev",
            Key.MediaStop => "MediaStop", Key.MediaPlayPause => "MediaPlayPause",
            Key.OemPlus => "OemPlus", Key.OemMinus => "OemMinus",
            Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown",
            Key.Back => "Backspace", Key.CapsLock => "CapsLock", Key.PrintScreen => "PrintScreen",
            Key.OemOpenBrackets => "OemOpenBrackets", Key.OemCloseBrackets => "OemCloseBrackets",
            Key.OemSemicolon => "OemSemicolon", Key.OemQuotes => "OemQuotes",
            Key.OemComma => "OemComma", Key.OemPeriod => "OemPeriod",
            Key.OemQuestion => "OemQuestion", Key.OemPipe => "OemPipe",
            Key.OemTilde => "OemTilde", Key.OemBackslash => "OemBackslash",
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

        // a key the registrar has no code for would be displayed as bound and never fire:
        // keep waiting for a usable combination instead of committing it
        if (!HotkeyManager.TryParse(text, out _, out _, out _)) return;

        HotkeyText = text;
        Disarm(text);
    }

    private void Disarm(string? hotkeyText)
    {
        _timeout?.Stop();
        _timeout = null;
        if (ReferenceEquals(_armedBox, this)) _armedBox = null;
        _armed = false;
        App.Hotkeys?.Resume();
        UpdateVisual();
        if (hotkeyText is not null)
            Committed?.Invoke(hotkeyText);
        else if (HotkeyText is null)
            Committed?.Invoke(null);   // Escape/timeout with no prior binding → stay empty
        // else: keep the existing binding untouched
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
