using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using SONO.App.Controls;
using SONO.App.ViewModels;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>One group card: color dot + name, big volume slider 0–100 + %, Mute toggle,
/// 3 hotkey boxes (Vol−/Vol+/Mute, right-click clears), member app chips, '+' assign picker.
/// Accepts app rows dragged from the Applications panel.</summary>
public sealed class ChannelCard : Border
{
    private readonly MixerVm _vm;
    private readonly ChannelVm _ch;
    private readonly Ellipse _dot;
    private readonly TextBlock _name;
    private readonly Slider _slider;
    private readonly TextBlock _pct;
    private readonly ToggleButton _muteBtn;
    private readonly HotkeyBox _hkDown, _hkUp, _hkMute;
    private readonly WrapPanel _chips;
    private bool _draggingSlider;
    private bool _dragHover;
    private readonly System.Diagnostics.Stopwatch _liveThrottle = new();
    private Thumb? _thumb;
    private IBrush? _thumbBrush;

    public ChannelCard(MixerVm vm, ChannelVm ch)
    {
        _vm = vm;
        _ch = ch;
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(14);
        Margin = new Thickness(4);

        _dot = new Ellipse { Width = 10, Height = 10 };
        _name = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _pct = new TextBlock { FontSize = 21, FontWeight = FontWeight.Bold, MinWidth = 60, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _slider = new Slider { Minimum = 0, Maximum = 100, MinHeight = 34 };
        // TUNNEL phase is critical: pressing the thumb gets the event marked handled by the
        // Thumb control, so a bubble handler never fires and the drag looks dead.
        _slider.AddHandler(PointerPressedEvent, (_, _) => { _draggingSlider = true; _liveThrottle.Restart(); }, RoutingStrategies.Tunnel);
        _slider.AddHandler(PointerReleasedEvent, (_, _) => CommitSlider(), RoutingStrategies.Tunnel);
        _slider.AddHandler(PointerCaptureLostEvent, (_, _) => CommitSlider(), RoutingStrategies.Bubble);
        _slider.ValueChanged += OnSliderLive;
        // Fluent paints the thumb from the SYSTEM accent and outranks local styles —
        // grab the templated Thumb directly and paint it ourselves
        _slider.TemplateApplied += (_, _) =>
        {
            _thumb = FindThumb(_slider);
            if (_thumb is not null && _thumbBrush is not null) _thumb.Background = _thumbBrush;
        };

        _muteBtn = new ToggleButton
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "sono" },
        };
        _muteBtn.IsCheckedChanged += (object? s, RoutedEventArgs e) =>
        {
            var m = _muteBtn.IsChecked == true;
            if (m != _ch.Muted) _ch.Muted = m;
        };

        _hkDown = MkHk(HotkeySlot.VolDown);
        _hkUp = MkHk(HotkeySlot.VolUp);
        _hkMute = MkHk(HotkeySlot.Mute);

        _chips = new WrapPanel();
        DragDrop.SetAllowDrop(this, true);   // REQUIRED in Avalonia: without it DragOver/Drop never fire
        Build();

        _ch.PropertyChanged += (_, e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncFromVm(e.PropertyName));
        _ch.Apps.CollectionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SyncChips);
        RefreshTheme();
        SyncFromVm(null);
        SyncChips();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private HotkeyBox MkHk(HotkeySlot slot)
    {
        var box = new HotkeyBox();
        box.Committed += text =>
        {
            var errs = _vm.SetHotkey(_ch.Id, slot, text);
            if (errs.Count > 0) SONO.Core.Diagnostics.Log.Write("hotkey errors: " + string.Join("; ", errs));
        };
        return box;
    }

    private void SyncFromVm(string? prop)
    {
        if (prop is null or nameof(ChannelVm.Volume) or nameof(ChannelVm.VolumePct))
        {
            _name.Text = _ch.Name;   // (was never set — names were invisible)
            _pct.Text = $"{_ch.VolumePct}%";
            if (!_draggingSlider) _slider.Value = _ch.VolumePct;
        }
        if (prop is null or nameof(ChannelVm.Muted))
        {
            _muteBtn.IsChecked = _ch.Muted;
            var iconImg = new Image
            {
                Source = UiIcon.Get(_ch.Muted ? "mute" : "sound"),
                Width = 22,
                Height = 22,
                Opacity = 0.95,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _muteBtn.Content = iconImg;
            _muteBtn.Background = TintBrush(GroupHex, _ch.Muted ? 0.45 : 0.25);   // group-colored button
            ToolTip.SetTip(_muteBtn, _ch.Muted ? "Unmute" : "Mute");
        }
        if (prop is null or nameof(ChannelVm.VolDownKey)) _hkDown.HotkeyText = _ch.VolDownKey;
        if (prop is null or nameof(ChannelVm.VolUpKey)) _hkUp.HotkeyText = _ch.VolUpKey;
        if (prop is null or nameof(ChannelVm.MuteKey)) _hkMute.HotkeyText = _ch.MuteKey;
    }

    private void CommitSlider()
    {
        _draggingSlider = false;
        var v = _slider.Value / 100.0;
        if (Math.Abs(v - _ch.Volume) > 0.001) _ch.Volume = v;
    }

    /// <summary>Live preview while dragging: apply to engine throttled (~15 Hz) so audio
    /// follows the thumb without flooding the session APIs every pixel.</summary>
    private void OnSliderLive(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_draggingSlider) return;
        _pct.Text = $"{(int)Math.Round(_slider.Value)}%";
        if (_liveThrottle.ElapsedMilliseconds < 65) return;
        _liveThrottle.Restart();
        var v = _slider.Value / 100.0;
        if (Math.Abs(v - _ch.Volume) > 0.001)
        {
            _ch.Volume = v;   // full path: settings + engine reconcile + save
        }
    }

    private void Build()
    {
        // ---- box 1: title + mute ----
        var titleBox = MkBox();
        var titleRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_muteBtn, Dock.Right);
        titleRow.Children.Add(_muteBtn);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(_dot);
        nameRow.Children.Add(_name);
        titleRow.Children.Add(nameRow);
        titleBox.Child = titleRow;

        // ---- box 2: volume only ----
        var volBox = MkBox(); volBox.Margin = new Thickness(0, 6, 0, 0);
        var volRow = new Grid();
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_slider, 0);
        Grid.SetColumn(_pct, 1);
        volRow.Children.Add(_slider);
        volRow.Children.Add(_pct);
        volBox.Child = volRow;

        // ---- box 3: apps ----
        var appsBox = MkBox(); appsBox.Margin = new Thickness(0, 6, 0, 0);
        var chipsHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chipsHead.Children.Add(new TextBlock { Text = "APPS", Classes = { "muted" }, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        DockPanel.SetDock(chipsHead, Dock.Top);
        var chipsScroll = new ScrollViewer { Content = _chips, MaxHeight = 300 };
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(chipsHead);
        dock.Children.Add(chipsScroll);
        appsBox.Child = dock;

        // ---- box 4: shortcuts ----
        var hkBox = MkBox(); hkBox.Margin = new Thickness(0, 6, 0, 0);
        var hkRow = new UniformGrid { Rows = 1, Columns = 3 };
        HkCell(_hkDown, "Vol −", hkRow);
        HkCell(_hkUp, "Vol +", hkRow);
        HkCell(_hkMute, "Mute", hkRow);
        hkBox.Child = hkRow;

        var sp = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(1, GridUnitType.Star), new RowDefinition(GridLength.Auto) } };
        Grid.SetRow(titleBox, 0);
        Grid.SetRow(volBox, 1);
        Grid.SetRow(appsBox, 2);
        Grid.SetRow(hkBox, 3);
        sp.Children.Add(titleBox);
        sp.Children.Add(volBox);
        sp.Children.Add(appsBox);
        sp.Children.Add(hkBox);
        Child = sp;
    }

    /// <summary>Inner box: tonal fill (one step below the card), rounded, no outline.</summary>
    private Border MkBox() => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10, 8),
        Background = BoxFill,
    };

    /// <summary>Inner box fill: translucent wash of the group's color over the card —
    /// hue stays clean (no opaque lerp mud), card tone shows through.</summary>
    private IBrush BoxFill
    {
        get
        {
            Color.TryParse(GroupHex, out var g);
            return new ImmutableSolidColorBrush(Color.FromArgb(46, g.R, g.G, g.B));
        }
    }

    private static void HkCell(Control box, string label, UniformGrid host)
    {
        // side margins create gutters between the three fields (first flush left, last flush right)
        var sp = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0) };
        sp.Children.Add(new TextBlock { Text = label, Classes = { "muted" }, FontSize = 10 });
        sp.Children.Add(box);
        host.Children.Add(sp);   // UniformGrid places children in order
    }

    // ---------------- chips ----------------

    private void SyncChips()
    {
        _chips.Children.Clear();
        foreach (var exe in _ch.Apps)
        {
            var e = exe;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new TextBlock { Text = e, FontSize = 11.5 });
            var x = new TextBlock { Text = "✕", FontSize = 10, Classes = { "muted" } };
            x.PointerPressed += (_, args) => { args.Handled = true; _vm.RemoveExe(e); };
            sp.Children.Add(x);
            chip.Child = sp;
            chip.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(chip).Properties.IsLeftButtonPressed)
                {
                    var d = new DataObject();
                    d.Set(DataFormats.Text, e);
                    DragDrop.DoDragDrop(args, d, DragDropEffects.Copy | DragDropEffects.Move);
                }
            };
            _chips.Children.Add(chip);
        }
        StyleChips();
    }

    private void StyleChips()
    {
        var p = Themes.ThemeManager.Current;
        IBrush bg = TintBrush(GroupHex, 0.35);          // group-tinted chips
        IBrush tx = Color.TryParse(p.Text, out var c2) ? new SolidColorBrush(c2) : Brushes.White;
        foreach (var ctrl in _chips.Children.OfType<Border>())
        {
            ctrl.Background = bg;
            if (ctrl.Child is StackPanel sp)
                foreach (var t in sp.Children.OfType<TextBlock>()) t.Foreground = tx;
        }
    }

    // ---------------- drag-drop ----------------

    
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.DragEffects != DragDropEffects.None && !_dragHover)
        {
            _dragHover = true;
            RefreshTheme();   // single source of truth for backgrounds
        }
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Avalonia fires spurious DragLeave during DragOver (re-hit-test on visual change) —
        // only honor it when the cursor truly left this card's bounds
        Win32Point p = default;
        GetCursorPos(ref p);
        var topLeft = this.PointToScreen(new Point(0, 0));
        double scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        var localX = (p.X - topLeft.X) / scale;
        var localY = (p.Y - topLeft.Y) / scale;
        if (localX >= 0 && localY >= 0 && localX <= Bounds.Width && localY <= Bounds.Height)
        {
            e.Handled = true;
            return;   // still inside — ignore the spurious leave
        }
        _dragHover = false;
        RefreshTheme();   // cursor left this card — clear the tint
        e.Handled = true;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref Win32Point pt);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragHover = false;
        RefreshTheme();   // clear the drag-over tint
        SONO.Core.Diagnostics.Log.Write($"drop {_ch.Name} text={e.Data.GetText()}");
        if (e.Data.GetText() is string exe && !string.IsNullOrWhiteSpace(exe))
        {
            _vm.AssignExe(exe, _ch.Id);
            e.Handled = true;
        }
    }

    private static IBrush TintBrush(string hex) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B)) : Brushes.Transparent;

    // ---------------- theme ----------------

    private string GroupHex
    {
        get
        {
            // theme GroupBgs 0-3 = Game/Chat/Media/Aux (position in the channel list)
            var gb = Themes.ThemeManager.Current.GroupBgs;
            int i = Math.Max(0, _vm.Channels.IndexOf(_ch));
            return i < gb.Length ? gb[i] : _ch.ColorHex;
        }
    }

    public void RefreshTheme()
    {
        var p = Themes.ThemeManager.Current;
        IBrush B(string hex) => Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Gray;
        // card body = the main window background (dark gray); identity comes from the outline.
        // While a drag hovers, fill with a lighter version of the group color
        Background = _dragHover ? new ImmutableSolidColorBrush(LighterColor(GroupHex, 0.30f)) : B(p.Bg);
        BorderBrush = Solid(GroupHex);
        BorderThickness = new Thickness(1.5);
        _dot.Fill = Solid(GroupHex);
        _name.Foreground = B(p.Text);
        _pct.Foreground = B(p.Text);
        _slider.Foreground = Solid(GroupHex);
        _thumbBrush = Lighter(GroupHex, 0.45f);   // thumb = lighter variant for contrast
        if (_thumb is not null) _thumb.Background = _thumbBrush;
        // shortcut inputs share the mute button's group tint
        var groupTint = TintBrush(GroupHex, 0.25);
        _hkDown.BackgroundOverride = groupTint;
        _hkUp.BackgroundOverride = groupTint;
        _hkMute.BackgroundOverride = groupTint;
        // inner boxes cache their fill — restyle them too
        foreach (var box in (Child as Grid)?.Children.OfType<Border>() ?? Enumerable.Empty<Border>())
            box.Background = BoxFill;
        StyleChips();
    }

    /// <summary>Opaque lerp between two hex colors — the tonal-blend look, no alpha stacking.</summary>
    private static IBrush Blend(string baseHex, string hex, float t)
    {
        Color.TryParse(baseHex, out var b);
        Color.TryParse(hex, out var c);
        return new ImmutableSolidColorBrush(Color.FromArgb(255,
            (byte)(b.R + (c.R - b.R) * t),
            (byte)(b.G + (c.G - b.G) * t),
            (byte)(b.B + (c.B - b.B) * t)));
    }

    private static IBrush Solid(string hex) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(c) : Brushes.White;

    /// <summary>Lightened Color (toward white) for drag-over states.</summary>
    private static Color LighterColor(string hex, float ratio)
    {
        Color.TryParse(hex, out var c);
        return Color.FromArgb(255,
            (byte)(c.R + (255 - c.R) * ratio),
            (byte)(c.G + (255 - c.G) * ratio),
            (byte)(c.B + (255 - c.B) * ratio));
    }

    private static Color Parse(string hex) => Color.TryParse(hex, out var c) ? c : Colors.Gray;

    private static IBrush TintBrush(string hex, double alpha) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)) : Brushes.Transparent;

    /// <summary>Darker Color (toward black) — slider fill derived from the box color.</summary>
    private static IBrush DarkerBrush(string hex, float ratio)
    {
        var c = Parse(hex);
        return new ImmutableSolidColorBrush(Color.FromArgb(255,
            (byte)(c.R * (1 - ratio)),
            (byte)(c.G * (1 - ratio)),
            (byte)(c.B * (1 - ratio))));
    }

    /// <summary>Lighten toward white by ratio (0 = same, 1 = white) — thumb gets a
    /// lighter shade of its channel color so it reads as part of the slider.</summary>
    private static IBrush Lighter(string hex, float ratio)
    {
        if (!Color.TryParse(hex, out var c)) return Brushes.White;
        return new ImmutableSolidColorBrush(Color.FromArgb(c.A,
            (byte)(c.R + (255 - c.R) * ratio),
            (byte)(c.G + (255 - c.G) * ratio),
            (byte)(c.B + (255 - c.B) * ratio)));
    }

    /// <summary>Depth-first search for the templated Thumb via the public logical tree
    /// (VisualChildren is protected; TemplateApplied gives us the tree through Content/Child).</summary>
    private static Thumb? FindThumb(object? node)
    {
        switch (node)
        {
            case Thumb t: return t;
            case ContentControl cc: return FindThumb(cc.Content);
            case Panel p:
                foreach (var child in p.Children)
                {
                    var f = FindThumb(child);
                    if (f is not null) return f;
                }
                return null;
            case Border b: return FindThumb(b.Child);
            default: return null;
        }
    }
}
