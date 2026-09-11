using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SONO.App.Themes;
using SONO.App.ViewModels;
using SONO.Core.SystemIntegrations;

namespace SONO.App;

/// <summary>Settings flyout window: Start-with-Windows, Start minimized, hotkey step,
/// OSD position, theme list, About.</summary>
public sealed class SettingsWindow : Window
{
    private readonly MixerVm _vm;
    internal bool _reallyClose;

    private const string Desc =
        "Per-app audio mixer for Windows — four groups (Game / Chat / Media / Aux) with group " +
        "volume, mute, global hotkeys and a volume OSD. No drivers needed.";

    private SettingsWindow(MixerVm vm)
    {
        _vm = vm;
        Title = "SONO Settings";
        Width = 440; Height = 520;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.Full;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = MainWindow.Res("SonoBgBrush");
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Inter");
        Content = Build();
    }

    /// <summary>Fired when a theme is picked here — main window repaints everything.</summary>
    public event Action? ThemeApplied;

    public static SettingsWindow Create(Window owner, MixerVm vm)
    {
        var w = new SettingsWindow(vm) { Owner = owner };
        return w;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!_reallyClose) { } // normal close from title bar is fine
    }

    private Control Build()
    {
        var sp = new StackPanel { Margin = new Thickness(16), Spacing = 8 };

        // ---- startup ----
        var startWin = new CheckBox { Content = "Start with Windows" };
        bool sw;
        lock (_vm.Settings) sw = _vm.Settings.StartWithWindows;
        startWin.IsChecked = sw;
        startWin.IsCheckedChanged += (_, _) =>
        {
            lock (_vm.Settings) _vm.Settings.StartWithWindows = startWin.IsChecked == true;
            try { Autostart.Set(startWin.IsChecked == true); }
            catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"autostart set: {ex.Message}"); }
            lock (_vm.Settings) _vm.Settings.AutostartConfigured = true;
            _vm.Save();
        };

        var startMin = new CheckBox { Content = "Start minimized to tray" };
        bool sm;
        lock (_vm.Settings) sm = _vm.Settings.StartMinimized;
        startMin.IsChecked = sm;
        startMin.IsCheckedChanged += (_, _) =>
        {
            lock (_vm.Settings) _vm.Settings.StartMinimized = startMin.IsChecked == true;
            _vm.Save();
        };

        // ---- hotkey step ----
        float step;
        lock (_vm.Settings) step = _vm.Settings.HotkeyStep;
        var stepBox = new NumericUpDown { Minimum = 1, Maximum = 10, Increment = 1, FormatString = "0", Width = 130,
            Value = (decimal)Math.Round(step * 100), HorizontalAlignment = HorizontalAlignment.Left };
        stepBox.ValueChanged += (_, _) =>
        {
            lock (_vm.Settings) _vm.Settings.HotkeyStep = (float)Math.Clamp((double)(stepBox.Value ?? 5m), 1, 10) / 100f;
            _vm.Save();
        };
        var stepRow = LabeledRow("Hotkey volume step (%)", stepBox);

        // ---- OSD anchor ----
        string anchor;
        lock (_vm.Settings) anchor = string.IsNullOrWhiteSpace(_vm.Settings.OsdAnchor) ? "bottom-right" : _vm.Settings.OsdAnchor;
        var osdBox = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var a in OsdAnchors) osdBox.Items.Add(DisplayAnchor(a));
        osdBox.SelectedIndex = Math.Max(0, Array.IndexOf(OsdAnchors, anchor));
        osdBox.SelectionChanged += (_, _) =>
        {
            var idx = osdBox.SelectedIndex >= 0 ? osdBox.SelectedIndex : 8;
            lock (_vm.Settings) _vm.Settings.OsdAnchor = OsdAnchors[idx];
            _vm.Save();
        };
        var osdRow = LabeledRow("Volume popup position", osdBox);

        // ---- theme (compact): dropdown + grid of pickers; 10 built-ins + 3 customs, all editable ----
        var allThemes = CustomThemeStore.AllWithCustoms();
        // PRISTINE snapshot for Reset: catalog entries are mutated in place during editing,
        // so the "original" must be copied NOW, before any picker touches them
        var pristineById = ThemeCatalog.All.ToDictionary(
            t => t.Id,
            t => CustomThemeStore.CloneAsCustom(t, "__snap"));
        string themeId;
        lock (_vm.Settings) themeId = _vm.Settings.ThemeId;
        var SyncAll = new List<Action>();   // swatch refreshers, run on theme switch
        var themeBox = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12.5 };
        foreach (var t in allThemes) themeBox.Items.Add(t.Name);
        var cur = allThemes.FirstOrDefault(t => t.Id == themeId) ?? allThemes[0];
        themeBox.SelectedIndex = Math.Max(0, allThemes.FindIndex(t => t.Id == cur.Id));

        Palette CurrentTheme() => allThemes.FirstOrDefault(t => t.Id == ThemeManager.Current.Id) ?? ThemeManager.Current;

        void ApplyCurrent()
        {
            ThemeManager.Apply(CurrentTheme());
            lock (_vm.Settings) _vm.Settings.ThemeId = ThemeManager.Current.Id;
            _vm.Save();
            ThemeApplied?.Invoke();
        }

        themeBox.SelectionChanged += (_, _) =>
        {
            if (themeBox.SelectedIndex < 0) return;
            var p = allThemes[themeBox.SelectedIndex];
            ThemeManager.Apply(p);
            lock (_vm.Settings) _vm.Settings.ThemeId = p.Id;
            _vm.Save();
            foreach (var r in SyncAll) r();   // swatches follow the newly-selected theme
            ThemeApplied?.Invoke();
        };
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        themeRow.Children.Add(new TextBlock { Text = "Theme", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        themeRow.Children.Add(themeBox);

        // reset: drop the saved override for the current slot (custom → deleted from file;
        // built-in → built-ins aren't in the file at all, so this only matters for customs)
        var resetBtn = new Button { Content = "Reset to default", Classes = { "sono" }, Padding = new Thickness(10, 5), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        resetBtn.Click += (_, _) =>
        {
            var currentId = ThemeManager.Current.Id;
            // remove any file override for this slot
            var customs = CustomThemeStore.Load();
            var removed = customs.RemoveAll(t => t.Id == currentId);
            if (removed > 0) CustomThemeStore.Save(customs);
            // restore from the PRE-EDIT snapshot (the catalog object itself was mutated in
            // place, so it can't be used as "pristine"), replace the stale entry in allThemes
            var pristine = pristineById.TryGetValue(currentId, out var snap)
                ? new Palette
                {
                    Id = currentId,
                    Name = allThemes.FirstOrDefault(t => t.Id == currentId)?.Name ?? snap.Name,
                    Bg = snap.Bg, Card = snap.Card, Elevated = snap.Elevated,
                    Field = snap.Field, FieldFocus = snap.FieldFocus, Chip = snap.Chip,
                    Border = snap.Border, Text = snap.Text, Muted = snap.Muted,
                    Accent = snap.Accent, Danger = snap.Danger,
                    Swatches = snap.Swatches.ToArray(), GroupBgs = snap.GroupBgs.ToArray(),
                    LogoUri = snap.LogoUri,
                }
                : ThemeCatalog.Default;
            var liveIdx = allThemes.FindIndex(t => t.Id == currentId);
            if (liveIdx >= 0) allThemes[liveIdx] = pristine;
            ThemeManager.Apply(pristine);
            lock (_vm.Settings) _vm.Settings.ThemeId = pristine.Id;
            _vm.Save();
            foreach (var r in SyncAll) r();   // force-refresh swatches + pickers
            ThemeApplied?.Invoke();
        };
        themeRow.Children.Add(resetBtn);

        // compact picker: a small color rectangle that opens a ColorView flyout
        StackPanel MakeColorEntry(string label, Func<Palette, string> get, Action<Palette, Color> set)
        {
            var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand) };
            var lbl = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var view = new Avalonia.Controls.ColorView
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ColorModel = Avalonia.Controls.ColorModel.Rgba,
                ColorSpectrumComponents = Avalonia.Controls.ColorSpectrumComponents.HueSaturation,
            };
            view.ColorChanged += (_, e) =>
            {
                set(CurrentTheme(), e.NewColor);
                swatch.Background = new SolidColorBrush(e.NewColor);
                ApplyCurrent();
                // persist ONLY the custom slots (built-ins are fixed)
                if (CurrentTheme().Id.StartsWith("custom-"))
                    CustomThemeStore.Save(allThemes.Where(t => t.Id.StartsWith("custom-")));
            };
            // host: no fixed size — measure the ColorView's natural size, pad around it
            var host = new Border
            {
                Background = MainWindow.Res("SonoElevatedBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = view,
            };
            var flyout = new Flyout
            {
                Placement = PlacementMode.Right,
                Content = host,
                HorizontalOffset = 8,
            };
            FlyoutBase.SetAttachedFlyout(swatch, flyout);
            swatch.PointerPressed += (_, _) =>
            {
                if (Color.TryParse(get(CurrentTheme()), out Color c)) view.Color = c;
                FlyoutBase.ShowAttachedFlyout(swatch);
            };
            SyncAll.Add(() =>
            {
                if (Color.TryParse(get(CurrentTheme()), out Color c))
                {
                    swatch.Background = new SolidColorBrush(c);
                    view.Color = c;   // reopened picker shows the restored color
                }
            });
            if (Color.TryParse(get(CurrentTheme()), out Color c0))
                swatch.Background = new SolidColorBrush(c0);
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            entry.Children.Add(swatch);
            entry.Children.Add(lbl);
            return entry;
        }

        var groupColors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        string[] glabels = { "Game", "Chat", "Media", "Aux" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            groupColors.Children.Add(MakeColorEntry(glabels[idx], p => p.GroupBgs[idx], (p, c) => p.GroupBgs[idx] = c.ToString()));
        }

        var surfaceColors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        surfaceColors.Children.Add(MakeColorEntry("Background", p => p.Bg, (p, c) => p.Bg = c.ToString()));
        surfaceColors.Children.Add(MakeColorEntry("Panels", p => p.Card, (p, c) => p.Card = c.ToString()));
        surfaceColors.Children.Add(MakeColorEntry("Elevated", p => p.Elevated, (p, c) => p.Elevated = c.ToString()));
        surfaceColors.Children.Add(MakeColorEntry("Field", p => p.Field, (p, c) => p.Field = c.ToString()));
        surfaceColors.Children.Add(MakeColorEntry("Text", p => p.Text, (p, c) => p.Text = c.ToString()));
        surfaceColors.Children.Add(MakeColorEntry("Muted", p => p.Muted, (p, c) => p.Muted = c.ToString()));

        var appearance = new StackPanel { Spacing = 10 };
        appearance.Children.Add(themeRow);
        appearance.Children.Add(new TextBlock { Text = "GROUPS", Classes = { "muted" }, FontSize = 10, FontWeight = FontWeight.SemiBold });
        appearance.Children.Add(groupColors);
        appearance.Children.Add(new TextBlock { Text = "SURFACES", Classes = { "muted" }, FontSize = 10, FontWeight = FontWeight.SemiBold });
        appearance.Children.Add(surfaceColors);

        // ---- about ----
        var about = new Border { Classes = { "card" }, Padding = new Thickness(14), CornerRadius = new CornerRadius(12) };
        var aboutSp = new StackPanel { Spacing = 6 };
        aboutSp.Children.Add(new TextBlock { Text = "SONO 2.0.0", FontSize = 16, FontWeight = FontWeight.Bold });
        aboutSp.Children.Add(new TextBlock { Text = Desc, TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 12 });
        var link = new Button { Content = "github.com/komabear/SONO-mixer", Classes = { "sono" }, Padding = new Thickness(8, 4) };
        link.Click += async (_, _) =>
        {
            try { await TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri("https://github.com/komabear/SONO-mixer"))!; }
            catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"launch link: {ex.Message}"); }
        };
        aboutSp.Children.Add(link);
        about.Child = aboutSp;

        sp.Children.Add(Section("Startup"));
        sp.Children.Add(startWin);
        sp.Children.Add(startMin);
        sp.Children.Add(Section("Behavior"));
        sp.Children.Add(stepRow);
        sp.Children.Add(osdRow);
        sp.Children.Add(Section("Appearance"));
        sp.Children.Add(appearance);
        sp.Children.Add(Section("About"));
        sp.Children.Add(about);

        return new ScrollViewer { Content = sp };
    }

    private static readonly string[] OsdAnchors =
    {
        "top-left", "top", "top-right", "left", "center", "right", "bottom-left", "bottom", "bottom-right", "off",
    };

    private static string DisplayAnchor(string a) => a switch
    {
        "off" => "Off",
        "top" => "Top center", "bottom" => "Bottom center", "left" => "Middle left", "right" => "Middle right",
        "center" => "Screen center",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(a.Replace('-', ' ')),
    };

    private static Palette CloneWith(Palette p, string? bg = null, string? card = null, string? text = null) => new()
    {
        Id = p.Id, Name = p.Name,
        Bg = bg ?? p.Bg, Card = card ?? p.Card, Elevated = p.Elevated,
        Field = p.Field, FieldFocus = p.FieldFocus, Chip = p.Chip,
        Border = p.Border, Text = text ?? p.Text, Muted = p.Muted,
        Accent = p.Accent, Danger = p.Danger,
        Swatches = p.Swatches.ToArray(), GroupBgs = p.GroupBgs.ToArray(), LogoUri = p.LogoUri,
    };

    private static Control Section(string title) => new TextBlock
    {
        Text = title.ToUpperInvariant(),
        Classes = { "muted" },
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private static Control LabeledRow(string label, Control field)
    {
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
        sp.Children.Add(field);
        return sp;
    }
}
