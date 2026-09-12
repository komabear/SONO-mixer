using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SONO.App.ViewModels;

namespace SONO.App;

/// <summary>Main mixer window — built 100% programmatically (no per-window AXAML).
/// Header: logo + title + OUTPUT picker + ⚙. Body: 2x2 channel cards + Applications panel.
/// Status bar at the bottom. Close button hides to tray; Exit lives in the tray menu.</summary>
public sealed class MainWindow : Window
{
    private readonly MixerVm _vm;
    private readonly TextBlock _status = new();
    private readonly ComboBox _outputBox = new() { MinWidth = 210, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _appsHost = new() { Spacing = 8 };
    private readonly Dictionary<string, ChannelCard> _cards = new();
    private readonly Dictionary<AppRowVm, Border> _appRows = new();
    private OsdWindow? _osd;
    private TrayIcon? _tray;
    private readonly Border _ghost = new()
    {
        IsVisible = false,
        IsHitTestVisible = false,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 8),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(140, 0, 0, 0), OffsetY = 6 }),
        Opacity = 0.92,
        ZIndex = 100,
        RenderTransform = new RotateTransform(6),
        Child = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
    };
    private bool _ghostOn;
    private Avalonia.Threading.DispatcherTimer? _ghostTimer;
    private bool _reallyClosing;
    private SettingsWindow? _settingsWin;
    private bool _outputBoxGuard;
    private Button? _gearBtn;
    private Border? _controlBox;
    private Border? _logoBox;

    public MainWindow(MixerVm vm)
    {
        _vm = vm;
        Title = "SONO Mixer";
        MinWidth = 880; MinHeight = 640;
        Width = 980; Height = 800;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Inter");
        RefreshTheme();

        // ---------- body (no header card) ----------
        var body = new Grid { Margin = new Thickness(16, 16, 16, 10) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14, GridUnitType.Pixel) });   // gutter
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300, GridUnitType.Pixel) });

        var leftCard = new Border { Classes = { "card" }, Child = BuildChannelGrid() };
        Grid.SetColumn(leftCard, 0);
        var rightColumn = BuildAppsPanel();
        Grid.SetColumn(rightColumn, 2);
        body.Children.Add(leftCard);
        body.Children.Add(rightColumn);

        // ---------- status: folded into the header tooltip area (no bottom bar) ----------
        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(body);

        // drag ghost: compositor-drawn overlay INSIDE the window (a separate top-level window
        // falls back to opaque transparency on some compositors → gray box behind corners)
        BuildGhost();
        Content = new Grid { Children = { root, _ghost } };

        _vm.OsdVolume += (id, vol) => ShowOsd(id, vol, null);
        _vm.OsdMute += (id, muted) => ShowOsd(id, null, muted);

        // VM → UI (ticks arrive on a threadpool thread)
        _vm.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.PropertyName == nameof(MixerVm.StatusText))
            {
                _status.Text = _vm.StatusText;   // status text kept for the tray/logging only
            }
            if (e.PropertyName == nameof(MixerVm.Outputs) || e.PropertyName == nameof(MixerVm.SelectedOutput))
                SyncOutputBox();
        });
        _vm.Apps.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(SyncAppRows);
        _vm.Channels.CollectionChanged += (_, _) => { };

        SyncOutputBox();
        SyncAppRows();
        _status.Text = _vm.StatusText;

        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);   // ghost follow (tunnel: beats children)

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _vm.Start();
                _vm.RefreshOutputs();
                _vm.RebindHotkeys();
                InitializeTray();
            }
            catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"startup: {ex}"); }
        }, DispatcherPriority.Loaded);
    }

    // ---------------- theme ----------------

    private void RefreshTheme()
    {
        var p = Themes.ThemeManager.Current;
        Background = Color.TryParse(p.Bg, out var c) ? new SolidColorBrush(c) : Brushes.Black;
    }

    internal void OnThemeChanged()
    {
        RefreshTheme();
        foreach (var card in _cards.Values) card.RefreshTheme();
        SyncAppRows();
        if (_controlBox is not null) _controlBox.Background = Res("SonoElevatedBrush");
        if (_logoBox is not null) _logoBox.Background = Res("SonoElevatedBrush");
        // refresh ONLY the tagged logo images (never other Images: mute/app-row/button icons)
        foreach (var img in this.GetVisualDescendants().OfType<Image>().Where(i => (i.Tag as string) == "sonoLogo"))
            UpdateLogo(img);
    }

    private static void UpdateLogo(Image img)
    {
        try
        {
            var uri = Themes.ThemeManager.Current.LogoUri;
            img.Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri(uri)));
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"logo load: {ex.Message}"); }
    }

    // ---------------- OSD ----------------

    private void ShowOsd(string channelId, double? vol, bool? muted)
    {
        var ch = _vm.Channels.FirstOrDefault(c => c.Id == channelId);
        if (ch is null) return;
        double v = vol ?? ch.Volume;
        bool m = muted ?? ch.Muted;
        string anchor;
        lock (_vm.Settings) anchor = string.IsNullOrWhiteSpace(_vm.Settings.OsdAnchor) ? "bottom-right" : _vm.Settings.OsdAnchor;
        if (anchor == "off") return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _osd ??= new OsdWindow();
                int oi = _vm.Channels.IndexOf(ch);
                OsdWindow.Show(_osd, anchor, ch.Name, ViewModels.GroupColors.Hex(oi), v, m);
            }
            catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"osd: {ex.Message}"); }
        });
    }

    // ---------------- channel grid ----------------

    private Control BuildChannelGrid()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < _vm.Channels.Count && i < 4; i++)
        {
            var card = new ChannelCard(_vm, _vm.Channels[i]);
            _cards[_vm.Channels[i].Id] = card;
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            grid.Children.Add(card);
        }
        return new ScrollViewer { Content = grid };
    }

    // ---------------- applications panel ----------------

    private Control BuildAppsPanel()
    {
        var title = new TextBlock { Text = "APPLICATIONS", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { Text = "drag onto a group", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var addBtn = UiIcon.IconButton("plus", 22);
        addBtn.Width = 34; addBtn.Height = 34; addBtn.VerticalAlignment = VerticalAlignment.Center;
        addBtn.Click += (_, _) =>
        {
            // picker = known apps not already pinned/shown — clicking pins a row into the panel
            var shown = _vm.Apps.Select(a => a.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> options;
            lock (_vm.Settings) options = _vm.Settings.KnownApps
                .Where(k => !shown.Contains(k) && k != "system")
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var items = new List<MenuItem>();
            if (options.Count == 0)
                items.Add(new MenuItem { Header = "(nothing to add — play an app first)", IsEnabled = false });
            else
                foreach (var exe in options)
                {
                    var e = exe;
                    items.Add(new MenuItem { Header = e, Command = new RelayCommand(() => _vm.PinApp(e)) });
                }
            new MenuFlyout { ItemsSource = items }.ShowAt(addBtn);
        };
        var titleRow = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 10) };
        DockPanel.SetDock(addBtn, Dock.Right);
        titleRow.Children.Add(addBtn);
        var titleSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleSp.Children.Add(title);
        titleSp.Children.Add(hint);
        titleRow.Children.Add(titleSp);
        // ---- top box: OUTPUT picker + settings gear ----
        var gear = UiIcon.IconButton("settings", 24);
        gear.Width = 36;
        gear.Height = 32;
        gear.VerticalAlignment = VerticalAlignment.Center;
        gear.Click += (_, _) => OpenSettings();
        _gearBtn = gear;
        _outputBox.SelectionChanged += OnOutputSelected;
        _outputBox.MinWidth = 150; _outputBox.MaxWidth = 200;
        var outLabel = new TextBlock { Text = "OUTPUT", Classes = { "muted" }, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var outRow = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto) } };
        var outSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { outLabel, _outputBox } };
        Grid.SetColumn(outSp, 0);
        Grid.SetColumn(gear, 1);
        outRow.Children.Add(outSp);
        outRow.Children.Add(gear);
        _controlBox = new Border
        {
            Classes = { "card" },
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10, 8),
            Background = MainWindow.Res("SonoElevatedBrush"),
            Child = outRow,
        };

        var head = new StackPanel { Margin = new Thickness(2, 2, 0, 10) };
        head.Children.Add(titleRow);

        // bottom of the panel: own box with just the logo (fills the box)
        var logo = new Image { MaxHeight = 110, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "sonoLogo", Stretch = Avalonia.Media.Stretch.Uniform };
        UpdateLogo(logo);
        _logoBox = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(8),
            Background = MainWindow.Res("SonoElevatedBrush"),
            Child = logo,
        };

        var appsCard = new Border
        {
            Classes = { "card" },
            Margin = new Thickness(0, 10, 0, 10),
            Child = new DockPanel { LastChildFill = true, Children = { head, new ScrollViewer { Content = _appsHost } } },
        };
        DockPanel.SetDock(head, Dock.Top);   // title row spans the top — rows get full width
        var panelGrid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(1, GridUnitType.Star), new RowDefinition(GridLength.Auto) } };
        Grid.SetRow(_controlBox, 0);
        Grid.SetRow(appsCard, 1);
        Grid.SetRow(_logoBox, 2);
        panelGrid.Children.Add(_controlBox);
        panelGrid.Children.Add(appsCard);
        panelGrid.Children.Add(_logoBox);

        return panelGrid;   // three stacked boxes: controlBox / applications card / logoBox
    }

    private void SyncAppRows()
    {
        _appsHost.Children.Clear();
        _appRows.Clear();
        foreach (var row in _vm.Apps)
        {
            var b = BuildAppRow(row);
            _appRows[row] = b;
            _appsHost.Children.Add(b);
        }
    }

    private Border BuildAppRow(AppRowVm row)
    {
        var name = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 108 };
        var tagText = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold };
        var mutedText = new TextBlock { Text = "MUTED", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Res("SonoDangerBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

        var tag = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, Child = tagText };

        var iconImage = new Image
        {
            Width = 16, Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        var icon = AppIcon.Get(row.Exe);
        if (icon is not null) iconImage.Source = icon; else iconImage.IsVisible = false;

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(iconImage);
        nameRow.Children.Add(name);

        var menuBtn = UiIcon.IconButton("more", 16);
        menuBtn.Width = 44; menuBtn.Height = 26; menuBtn.VerticalAlignment = VerticalAlignment.Center;
        menuBtn.Margin = new Thickness(8, 0, 0, 0);   // spacer to the tag/name

        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(menuBtn, Dock.Right);
        DockPanel.SetDock(tag, Dock.Right);
        DockPanel.SetDock(mutedText, Dock.Right);
        top.Children.Add(menuBtn);
        top.Children.Add(tag);
        top.Children.Add(mutedText);
        top.Children.Add(nameRow);

        // bottom row: percentage right-aligned to the title's right edge, bar stretches
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, CornerRadius = new CornerRadius(3) };
        var volLabel = new TextBlock { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        var bottom = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(volLabel, 1);
        bottom.Children.Add(bar);
        bottom.Children.Add(volLabel);

        var sp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(top);
        sp.Children.Add(bottom);

        menuBtn.Click += (_, _) => OpenAppAssignMenu(menuBtn, row);

        var root = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Child = sp;

        void Update(AppRowVm r)
        {
            name.Text = r.Name;
            tagText.Text = r.GroupName;
            tag.IsVisible = r.HasGroup;
            mutedText.IsVisible = r.SessionMuted;   // state on the left of the group tag
            bar.Value = Math.Round(r.SessionVol * 100);
            volLabel.Text = $"{Math.Round(r.SessionVol * 100)}%";
            var idx = r.Group is null ? -1 : _vm.Channels.ToList().FindIndex(c => c.Id == r.Group);
            name.Foreground = Res("SonoTextBrush");   // Fluent default can be dark-on-dark
            volLabel.Foreground = Res("SonoMutedBrush");
            if (idx < 0)
            {
                root.Background = Res("SonoElevatedBrush");
                root.BorderThickness = new Thickness(0);
                root.BorderBrush = null;
                bar.Foreground = Res("SonoMutedBrush");
            }
            else
            {
                var hex = ViewModels.GroupColors.Hex(idx);
                root.Background = Tint(hex);
                root.BorderBrush = Solid(hex);
                root.BorderThickness = new Thickness(1.2);
                tagText.Foreground = Solid(hex);
                bar.Foreground = Solid(hex);   // volume bar carries the group color
            }
        }
        Update(row);
        row.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() => Update(row));

        // threshold-based drag start: the synchronous DoDragDrop-inside-PointerPressed
        // pattern is flaky in Avalonia; the reliable path is track → threshold → drag
        IPointer? dragPointer = null;
        Point dragStart = default;
        const double dragThreshold = 5.0;
        root.PointerPressed += (object? s, PointerPressedEventArgs e) =>
        {
            if (e.GetCurrentPoint(root).Properties.IsLeftButtonPressed && dragPointer is null)
            {
                dragPointer = e.Pointer;
                dragStart = e.GetPosition(root);
            }
        };
        root.PointerMoved += (object? s, PointerEventArgs e) =>
        {
            if (dragPointer != e.Pointer) return;
            if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) { dragPointer = null; GhostHide(); return; }
            var delta = e.GetPosition(root) - dragStart;
            if (Math.Abs(delta.X) < dragThreshold && Math.Abs(delta.Y) < dragThreshold) return;
            dragPointer = null;
            var d = new DataObject();
            d.Set(DataFormats.Text, row.Exe);
            int gi = row.Group is null ? -1 : _vm.Channels.ToList().FindIndex(c => c.Id == row.Group);
            GhostShow(row.Name, gi >= 0 ? ViewModels.GroupColors.Hex(gi) : null);
            // DoDragDrop is ASYNC — hide the ghost when the drag actually ends
            DragDrop.DoDragDrop(e, d, DragDropEffects.Copy | DragDropEffects.Move)
                .ContinueWith(t =>
                {
                    SONO.Core.Diagnostics.Log.Write($"drag ended result={t.Result}");
                    Dispatcher.UIThread.Post(GhostHide);
                });
        };
        root.PointerReleased += (object? s, PointerReleasedEventArgs e) => dragPointer = null;
        // visual affordance: rows with a grab feel
        root.Cursor = new Cursor(StandardCursorType.Hand);

        return root;
    }

    private static IBrush Tint(string hex) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(Color.FromArgb(64, c.R, c.G, c.B)) : Brushes.Transparent;

    private static IBrush Solid(string hex) =>
        Color.TryParse(hex, out var c) ? new ImmutableSolidColorBrush(c) : Brushes.White;

    private void OpenAppAssignMenu(Control parent, AppRowVm row)
    {
        var items = new List<MenuItem>();
        foreach (var ch in _vm.Channels)
        {
            var target = ch;
            items.Add(new MenuItem
            {
                Header = (ch.Id == row.Group ? "● " : "") + ch.Name,
                Command = new RelayCommand(() => _vm.AssignExe(row.Exe, target.Id)),
            });
        }
        if (row.Group is not null)
            items.Add(new MenuItem { Header = "Remove from group", Command = new RelayCommand(() => _vm.RemoveExe(row.Exe)) });
        new MenuFlyout { ItemsSource = items }.ShowAt(parent);
    }

    // ---------------- drag ghost (in-window overlay) ----------------

    private void BuildGhost() { }   // ghost constructed inline as a field; overlay wired in ctor

    private void GhostShow(string text, string? colorHex)
    {
        var label = (TextBlock)_ghost.Child!;
        label.Text = text;
        Color.TryParse(colorHex, out var c);
        _ghost.Background = new SolidColorBrush(c == default
            ? Color.FromArgb(225, 60, 64, 78)
            : Color.FromArgb(205, c.R, c.G, c.B));
        _ghost.IsVisible = true;
        _ghostOn = true;
        GhostFollow();
        // OLE drag loops swallow pointer events — poll the cursor instead
        _ghostTimer ??= new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render,
            (_, _) => GhostFollow());
        _ghostTimer.Start();
    }

    private void GhostFollow()
    {
        if (!_ghostOn || PlatformImpl is null) return;
        // cursor in window coordinates; ghost floats right-below the pointer
        Win32Point p = default;
        GetCursorPos(ref p);
        var pos = Position;
        var local = new Point(p.X - pos.X - 46, p.Y - pos.Y - 8);
        _ghost.Margin = new Thickness(local.X, local.Y, 0, 0);
        _ghost.HorizontalAlignment = HorizontalAlignment.Left;
        _ghost.VerticalAlignment = VerticalAlignment.Top;
    }

    private void GhostHide()
    {
        _ghostOn = false;
        _ghostTimer?.Stop();
        _ghost.IsVisible = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_ghostOn) GhostFollow();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref Win32Point pt);

    // ---------------- output picker ----------------

    private void SyncOutputBox()
    {
        _outputBoxGuard = true;
        _outputBox.ItemsSource = _vm.Outputs.Select(o => o.Name).ToList();
        var sel = _vm.SelectedOutput;
        _outputBox.SelectedIndex = sel is null ? -1 : _vm.Outputs.IndexOf(sel);
        _outputBoxGuard = false;
    }

    private void OnOutputSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_outputBoxGuard) return;
        var idx = _outputBox.SelectedIndex;
        if (idx < 0 || idx >= _vm.Outputs.Count) return;
        var target = _vm.Outputs[idx];
        if (ReferenceEquals(target, _vm.SelectedOutput)) return;
        _vm.SelectedOutput = target;   // setter switches default + saves
    }

    // ---------------- settings ----------------

    private void OpenSettings()
    {
        if (_settingsWin is { IsVisible: true }) { _settingsWin.Activate(); return; }
        _settingsWin = SettingsWindow.Create(this, _vm);
        _settingsWin.ThemeApplied += OnThemeChanged;
        _settingsWin.Closed += (_, _) => _settingsWin = null;
        _settingsWin.Show(this);
    }

    // ---------------- tray ----------------

    private void InitializeTray()
    {
        _tray = new TrayIcon();
        _tray.Click += () => Dispatcher.UIThread.Post(ShowFromTray);
        _tray.MenuRequested += () => Dispatcher.UIThread.Post(ShowTrayMenu);
    }

    private void ShowTrayMenu()
    {
        if (_tray is null) return;
        _vm.RefreshOutputs();
        var outputs = _vm.Outputs.ToList();
        var sel = _vm.SelectedOutput;
        var items = new List<(string, Action, bool, bool, bool)>
        {
            ("Open SONO", ShowFromTray, false, false, false),
            ("Mute all channels", () => Dispatcher.UIThread.Post(() => SetAllMuted(true)), false, false, true),
            ("Unmute all channels", () => Dispatcher.UIThread.Post(() => SetAllMuted(false)), false, false, false),
        };
        foreach (var o in outputs)
        {
            var output = o;
            bool current = sel?.Id == o.Id;
            items.Add(((current ? "●  " : "") + o.Name,
                () => Dispatcher.UIThread.Post(() => _vm.SelectedOutput = output),
                current, false, ReferenceEquals(o, outputs.FirstOrDefault())));
        }
        items.Add(("Exit", () => Dispatcher.UIThread.Post(ExitApp), false, false, true));
        _tray.PopupMenu(items);
    }

    private void SetAllMuted(bool muted)
    {
        foreach (var ch in _vm.Channels) ch.Muted = muted;
    }

    private void ExitApp()
    {
        _reallyClosing = true;
        Close();
    }

    private void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        var wa = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        if (Position.X < wa.X - Width || Position.Y < wa.Y - Height || Position.X > wa.Right || Position.Y > wa.Bottom)
            Position = new PixelPoint(wa.X + 40, wa.Y + 40);
        Activate();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settingsWin is { IsVisible: true }) { _settingsWin.Close(); }
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _osd?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
    }

    // ---------------- helpers ----------------

    internal static IBrush Res(string key)
    {
        if (Application.Current is not null &&
            Application.Current.TryGetResource(key, null, out var v) && v is IBrush b) return b;
        return Brushes.Transparent;
    }
}

/// <summary>Tiny ICommand shim for menu items.</summary>
internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
