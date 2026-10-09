using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using SONO.App.Controls;
using SONO.App.ViewModels;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>One group channel strip (vertical, DAW-style): color dot, name, Mute toggle,
/// vertical fader with round thumb + %, app count, and 3 stacked hotkey slots
/// (Vol−/Vol+/Mute — click to bind, right-click clears). Accepts app rows dragged
/// from the Applications panel; highlights in its own color while hovered.</summary>
public sealed class ChannelStrip : Border
{
    private readonly MixerVm _vm;
    private readonly ChannelVm _ch;
    private readonly Ellipse _dot;
    private readonly TextBlock _name;
    private readonly Fader _fader;          // custom vertical fader (round thumb, never overflows)
    private readonly TextBlock _pct;
    private readonly ToggleButton _muteBtn;
    private readonly TextBlock _count;
    private readonly HotkeyBox _hkDown, _hkUp, _hkMute;
    private StackPanel? _hksHost;
    private readonly List<Border> _hkRowBorders = new();
    private bool _dragHover;
    private static ChannelStrip? _hovered;  // the one strip currently showing the drag tint

    /// <summary>Clear the drag tint on whichever strip has it (DragLeave is unreliable between strips).</summary>
    internal static void ClearDragHover()
    {
        var c = _hovered;
        _hovered = null;
        if (c is not null && c._dragHover) { c._dragHover = false; c.RefreshTheme(); }
    }

    public ChannelStrip(MixerVm vm, ChannelVm ch)
    {
        _vm = vm;
        _ch = ch;
        Padding = new Thickness(10, 14);
        CornerRadius = new CornerRadius(12);
        Margin = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Stretch;

        _dot = new Ellipse { Width = 11, Height = 11, HorizontalAlignment = HorizontalAlignment.Center };
        _name = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
            Height = 18,
        };
        _pct = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _count = new TextBlock { FontSize = 10.5, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

        _muteBtn = new ToggleButton
        {
            Width = 30,
            Height = 22,
            Padding = new Thickness(0),
            Content = new TextBlock { Text = "M", FontSize = 10, FontWeight = FontWeight.Bold },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _muteBtn.IsCheckedChanged += (_, _) =>
        {
            var m = _muteBtn.IsChecked == true;
            if (m != _ch.Muted) _ch.Muted = m;
        };

        _fader = new Fader
        {
            Minimum = 0,
            Maximum = 100,
            Width = 88,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _fader.ValueChanged += () =>
        {
            _pct.Text = $"{(int)Math.Round(_fader.Value)}%";
        };
        _fader.CommitRequested += () =>
        {
            var v = _fader.Value / 100.0;
            if (Math.Abs(v - _ch.Volume) > 0.001) _ch.Volume = v;
        };
        _fader.LiveRequested += () =>
        {
            var v = _fader.Value / 100.0;
            if (Math.Abs(v - _ch.Volume) > 0.001) _ch.Volume = v;   // engine reconciles (throttled by VM)
        };

        _hkDown = MkHk(HotkeySlot.VolDown);
        _hkUp = MkHk(HotkeySlot.VolUp);
        _hkMute = MkHk(HotkeySlot.Mute);

        DragDrop.SetAllowDrop(this, true);   // REQUIRED in Avalonia: without it DragOver/Drop never fire
        Build();

        _ch.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() => SyncFromVm(e.PropertyName));
        _ch.Apps.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(SyncCount);
        RefreshTheme();
        SyncFromVm(null);
        SyncCount();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AttachedToVisualTree += (_, _) => HookWindowBounds();
    }

    /// <summary>Track the host window's height: below 540px the hotkey slots hide
    /// (faders + apps panel win the space).</summary>
    private void HookWindowBounds()
    {
        if (VisualRoot is Window win)
        {
            win.PropertyChanged -= OnWindowSizeChanged;
            win.PropertyChanged += OnWindowSizeChanged;
            ApplyHeightPolicy(win.Bounds.Height);
        }
    }

    private void OnWindowSizeChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "Bounds" && sender is Window win)
            ApplyHeightPolicy(win.Bounds.Height);
    }

    private void ApplyHeightPolicy(double windowHeight)
    {
        if (_hksHost is null) return;
        bool show = windowHeight >= 540;
        if (_hksHost.IsVisible != show)
        {
            _hksHost.IsVisible = show;
            InvalidateMeasure();
        }
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
            _name.Text = _ch.Name;
            _pct.Text = $"{_ch.VolumePct}%";
            _fader.Value = _ch.VolumePct;   // Fader suppresses re-entrant commit internally
        }
        if (prop is null or nameof(ChannelVm.Muted))
        {
            _muteBtn.IsChecked = _ch.Muted;
        }
        if (prop is null or nameof(ChannelVm.VolDownKey)) _hkDown.HotkeyText = _ch.VolDownKey;
        if (prop is null or nameof(ChannelVm.VolUpKey)) _hkUp.HotkeyText = _ch.VolUpKey;
        if (prop is null or nameof(ChannelVm.MuteKey)) _hkMute.HotkeyText = _ch.MuteKey;
    }

    private void SyncCount()
    {
        int n = _ch.Apps.Count;
        _count.Text = n == 0 ? "no apps" : n == 1 ? "1 app" : $"{n} apps";
    }

    private void Build()
    {
        // stacked hotkey slots at the bottom: label left, key right (three full-width rows).
        // Hidden entirely when the window is shorter than 540px — space is too tight.
        var hks = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Bottom };
        hks.Children.Add(HkRow(_hkDown, "VOL−"));
        hks.Children.Add(HkRow(_hkUp, "VOL+"));
        hks.Children.Add(HkRow(_hkMute, "MUTE"));
        _hksHost = hks;

        // fader grows to fill everything between the header block and the bottom block
        var sp = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(1, GridUnitType.Star), new RowDefinition(GridLength.Auto) }, RowSpacing = 10 };
        var head = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        head.Children.Add(_dot);
        head.Children.Add(_name);
        head.Children.Add(_muteBtn);
        Grid.SetRow(head, 0);

        var mid = new Grid { RowDefinitions = { new RowDefinition(1, GridUnitType.Star), new RowDefinition(GridLength.Auto) }, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(_fader, 0);
        Grid.SetRow(_pct, 1);
        _pct.VerticalAlignment = VerticalAlignment.Bottom;
        _pct.Margin = new Thickness(0, 4, 0, 0);
        mid.Children.Add(_fader);
        mid.Children.Add(_pct);
        Grid.SetRow(mid, 1);

        var bottom = new StackPanel { Spacing = 8 };
        _count.Height = 16;
        _count.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_count);
        bottom.Children.Add(hks);
        Grid.SetRow(bottom, 2);

        sp.Children.Add(head);
        sp.Children.Add(mid);
        sp.Children.Add(bottom);
        Child = sp;
    }

    private Border HkRow(HotkeyBox box, string label)
    {
        var lbl = new TextBlock { Text = label, FontSize = 8.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(1, GridUnitType.Star) }, Margin = new Thickness(0) };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(box, 1);
        row.Children.Add(lbl);
        row.Children.Add(box);
        // FIXED height: hotkey text changes must never reflow the strip (which resizes the fader)
        var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 2), Height = 30, Child = row };
        _hkRowBorders.Add(b);
        return b;
    }

    // ---------------- drag-drop ----------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.DragEffects != DragDropEffects.None && !_dragHover)
        {
            if (_hovered is not null && !ReferenceEquals(_hovered, this)) ClearDragHover();
            _hovered = this;
            _dragHover = true;
            RefreshTheme();
        }
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Avalonia fires spurious DragLeave during DragOver (re-hit-test on visual change) —
        // only honor it when the cursor truly left this strip's bounds
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
        RefreshTheme();
        e.Handled = true;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref Win32Point pt);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragHover = false;
        if (ReferenceEquals(_hovered, this)) _hovered = null;
        RefreshTheme();
        SONO.Core.Diagnostics.Log.Write($"drop {_ch.Name} text={e.Data.GetText()}");
        if (e.Data.GetText() is string exe && !string.IsNullOrWhiteSpace(exe))
        {
            _vm.AssignExe(exe, _ch.Id);
            e.Handled = true;
        }
    }

    // ---------------- theme ----------------

    private string GroupHex
    {
        get
        {
            // single source of truth — same as app rows, ghost, OSD
            int i = Math.Max(0, _vm.Channels.IndexOf(_ch));
            return ViewModels.GroupColors.Hex(i, _ch.ColorHex);
        }
    }

    public void RefreshTheme()
    {
        var p = Themes.ThemeManager.Current;
        IBrush B(string hex) => Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Gray;
        // body = card tone; drag-hover = light wash of the group color
        Background = _dragHover ? TintBrush(GroupHex, 0.22) : B(p.Card);
        _dot.Fill = Solid(GroupHex);
        _name.Foreground = B(p.Text);
        _pct.Foreground = B(p.Text);
        _count.Foreground = B(p.Muted);
        _fader.GroupColor = GroupHex;
        _muteBtn.Background = TintBrush(GroupHex, _ch.Muted ? 0.45 : 0.22);
        // hotkey rows: transparent (no background) — text follows the theme text color
        foreach (var b in _hkRowBorders) b.Background = Brushes.Transparent;
        _hkDown.BackgroundOverride = null;
        _hkUp.BackgroundOverride = null;
        _hkMute.BackgroundOverride = null;
        _hkDown.RefreshTheme();
        _hkUp.RefreshTheme();
        _hkMute.RefreshTheme();
    }

    /// <summary>Opaque blend of the group color toward black — the "darker channel shade".</summary>
    private static IBrush DarkerBlend(string hex, float keepRatio)
    {
        if (!Color.TryParse(hex, out var c)) return new ImmutableSolidColorBrush(Color.FromRgb(30, 32, 38));
        var bg = Color.TryParse(Themes.ThemeManager.Current.Bg, out var b) ? b : Color.FromRgb(22, 24, 30);
        // mix: 55% toward the theme bg from the group color (per-theme darkness anchor)
        byte Mix(byte gc, byte bc) => (byte)(gc * keepRatio + bc * (1 - keepRatio));
        return new ImmutableSolidColorBrush(Color.FromRgb(Mix(c.R, bg.R), Mix(c.G, bg.G), Mix(c.B, bg.B)));
    }

    private static IBrush Solid(string hex) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(c) : Brushes.White;

    private static IBrush TintBrush(string hex, double alpha) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)) : Brushes.Transparent;
}
