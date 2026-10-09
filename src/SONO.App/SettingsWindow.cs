using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using SONO.App.Controls;
using SONO.App.Themes;
using SONO.App.ViewModels;
using SONO.Core.SystemIntegrations;

namespace SONO.App;

/// <summary>Settings window: Startup, Behavior, Outputs (visibility + prev/next hotkeys),
/// Appearance (theme + palette editor), About. Compact card-based layout matching the mixer.</summary>
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
        Width = 560; Height = 640;
        CanResize = true;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.Full;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = MainWindow.Res("SonoBgBrush");
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Inter");
        Content = Build();
        // live theme switch: refresh window bg + every Fluent control that ignores DynamicResource
        Themes.ThemeManager.ThemeChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                Background = MainWindow.Res("SonoBgBrush");
                foreach (var b in this.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("sono")))
                {
                    b.Background = MainWindow.Res("SonoFieldBrush");
                    b.Foreground = MainWindow.Res("SonoTextBrush");
                }
                foreach (var cb in this.GetVisualDescendants().OfType<CheckBox>())
                    cb.Foreground = MainWindow.Res("SonoTextBrush");
                foreach (var cbi in this.GetVisualDescendants().OfType<ComboBox>())
                {
                    cbi.Foreground = MainWindow.Res("SonoTextBrush");
                    cbi.Background = MainWindow.Res("SonoFieldBrush");
                }
                foreach (var card in this.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("card")))
                    card.Background = MainWindow.Res("SonoCardBrush");
                }
                catch (Exception ex) { SONO.Core.Diagnostics.Log.Write("settings restyle: " + ex.Message); }
            });
    }

    /// <summary>Fired when a theme is picked here — main window repaints everything.</summary>
    public event Action? ThemeApplied;

    public static SettingsWindow Create(Window owner, MixerVm vm)
    {
        var w = new SettingsWindow(vm) { Owner = owner };
        return w;
    }

    private Control Build()
    {
        // every TextBlock inside this window follows the theme text color
        Resources["TextBlockForeground"] = MainWindow.Res("SonoTextBrush");
        var sp = new StackPanel { Margin = new Thickness(14), Spacing = 10 };

        // ================= STARTUP =================
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

        var startupCard = new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 8, Children = { Section("STARTUP"), startWin, startMin } } };

        // ================= BEHAVIOR =================
        float step;
        lock (_vm.Settings) step = _vm.Settings.HotkeyStep;
        var stepBox = new NumericUpDown { Minimum = 1, Maximum = 10, Increment = 1, FormatString = "0", Width = 110,
            Value = (decimal)Math.Round(step * 100), HorizontalAlignment = HorizontalAlignment.Left };
        stepBox.ValueChanged += (_, _) =>
        {
            lock (_vm.Settings) _vm.Settings.HotkeyStep = (float)Math.Clamp((double)(stepBox.Value ?? 5m), 1, 10) / 100f;
            _vm.Save();
        };

        string anchor;
        lock (_vm.Settings) anchor = string.IsNullOrWhiteSpace(_vm.Settings.OsdAnchor) ? "bottom-right" : _vm.Settings.OsdAnchor;
        var osdBox = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var a in OsdAnchors) osdBox.Items.Add(DisplayAnchor(a));
        osdBox.SelectedIndex = Math.Max(0, Array.IndexOf(OsdAnchors, anchor));
        osdBox.SelectionChanged += (_, _) =>
        {
            var idx = osdBox.SelectedIndex >= 0 ? osdBox.SelectedIndex : 8;
            lock (_vm.Settings) _vm.Settings.OsdAnchor = OsdAnchors[idx];
            _vm.Save();
        };

        var behaviorCard = new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Section("BEHAVIOR"),
                    Row("Hotkey volume step", stepBox),
                    Row("Volume popup position", osdBox),
                },
            },
        };

        // ================= OUTPUTS =================
        var outputsCard = new Border { Classes = { "card" } };
        var outputsSp = new StackPanel { Spacing = 10 };
        outputsCard.Child = outputsSp;
        outputsSp.Children.Add(Section("OUTPUTS"));

        // visibility list (rebuilt on demand)
        var visibilityHost = new StackPanel { Spacing = 6 };
        outputsSp.Children.Add(new TextBlock { Text = "Visible on the main window dropdown", Classes = { "muted" }, FontSize = 11 });
        outputsSp.Children.Add(visibilityHost);

        void RebuildVisibilityList()
        {
            visibilityHost.Children.Clear();
            List<OutputVm> all;
            try
            {
                using var enumr = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                all = enumr.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
                    .Select(d => { var vm = new OutputVm(d.ID, d.FriendlyName); d.Dispose(); return vm; })
                    .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                visibilityHost.Children.Add(new TextBlock { Text = $"Could not list devices: {ex.Message}", Classes = { "muted" }, FontSize = 11 });
                return;
            }

            foreach (var o in all)
            {
                var dev = o;
                var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                lock (_vm.Settings)
                    cb.IsChecked = OutputVisibility.IsVisible(_vm.Settings, dev.Id);
                var name = new TextBlock { Text = dev.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(cb);
                row.Children.Add(name);
                cb.IsCheckedChanged += (_, _) =>
                {
                    bool vis = cb.IsChecked == true;
                    lock (_vm.Settings)
                    {
                        // never hide the active output
                        if (!vis && _vm.SelectedOutput?.Id == dev.Id) { cb.IsChecked = true; return; }
                        OutputVisibility.SetVisible(_vm.Settings, dev.Id, vis);
                    }
                    _vm.Save();
                    _vm.RefreshOutputs();   // main dropdown updates live
                };
                visibilityHost.Children.Add(row);
            }
        }
        RebuildVisibilityList();

        outputsSp.Children.Add(new TextBlock { Text = "SWITCH HOTKEYS", Classes = { "muted" }, FontSize = 10, FontWeight = FontWeight.SemiBold });

        // prev/next hotkey capture boxes (same widget as the channel cards)
        var prevBox = new HotkeyBox();
        var nextBox = new HotkeyBox();
        lock (_vm.Settings)
        {
            prevBox.HotkeyText = _vm.Settings.OutputPrevHotkey;
            nextBox.HotkeyText = _vm.Settings.OutputNextHotkey;
        }
        prevBox.Committed += text => { lock (_vm.Settings) _vm.Settings.OutputPrevHotkey = text; _vm.Save(); _vm.RebindHotkeys(); };
        nextBox.Committed += text => { lock (_vm.Settings) _vm.Settings.OutputNextHotkey = text; _vm.Save(); _vm.RebindHotkeys(); };
        var hkRow = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(1, GridUnitType.Star) } };
        var prevCell = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 4, 0), Children = { new TextBlock { Text = "Previous output", Classes = { "muted" }, FontSize = 10 }, prevBox } };
        var nextCell = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0, 0, 0), Children = { new TextBlock { Text = "Next output", Classes = { "muted" }, FontSize = 10 }, nextBox } };
        Grid.SetColumn(prevCell, 0);
        Grid.SetColumn(nextCell, 1);
        hkRow.Children.Add(prevCell);
        hkRow.Children.Add(nextCell);
        outputsSp.Children.Add(hkRow);

        // ================= APPEARANCE =================
        var allThemes = CustomThemeStore.AllWithCustoms();
        var pristineById = ThemeCatalog.All.ToDictionary(
            t => t.Id,
            t => CustomThemeStore.CloneAsCustom(t, "__snap"));
        string themeId;
        lock (_vm.Settings) themeId = _vm.Settings.ThemeId;
        var SyncAll = new List<Action>();
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
            foreach (var r in SyncAll) r();
            ThemeApplied?.Invoke();
        };

        var resetBtn = new Button { Content = "Reset to default", Classes = { "sono" }, Padding = new Thickness(10, 5), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        resetBtn.Click += (_, _) =>
        {
            var currentId = ThemeManager.Current.Id;
            var customs = CustomThemeStore.Load();
            var removed = customs.RemoveAll(t => t.Id == currentId);
            if (removed > 0) CustomThemeStore.Save(customs);
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
            foreach (var r in SyncAll) r();
            ThemeApplied?.Invoke();
        };

        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        themeRow.Children.Add(new TextBlock { Text = "Theme", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        themeRow.Children.Add(themeBox);
        themeRow.Children.Add(resetBtn);

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
                if (get(CurrentTheme()).Equals(e.NewColor.ToString(), StringComparison.OrdinalIgnoreCase))
                    return;   // no-op color event (theme refresh etc.) — don't save-storm
                set(CurrentTheme(), e.NewColor);
                swatch.Background = new SolidColorBrush(e.NewColor);
                ApplyCurrent();
                // store keeps customs always + built-ins that differ from catalog
                CustomThemeStore.Save(allThemes);
            };
            var host = new Border
            {
                Background = MainWindow.Res("SonoElevatedBrush"),
                CornerRadius = new CornerRadius(10),
                Width = 356,
                Height = 420,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = view,
            };
            var flyout = new Flyout { Placement = PlacementMode.Right, Content = host, HorizontalOffset = 8 };
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
                    view.Color = c;
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

        var appearanceCard = new Border { Classes = { "card" }, Child = appearance };

        // ================= ABOUT =================
        var about = new Border { Classes = { "card" }, Padding = new Thickness(14), CornerRadius = new CornerRadius(12) };
        var aboutSp = new StackPanel { Spacing = 6 };
        aboutSp.Children.Add(new TextBlock { Text = "SONO 3.2.0", FontSize = 16, FontWeight = FontWeight.Bold });
        aboutSp.Children.Add(new TextBlock { Text = Desc, TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 12 });
        var link = new Button { Content = "github.com/komabear/SONO-mixer", Classes = { "sono" }, Padding = new Thickness(8, 4) };
        link.Click += async (_, _) =>
        {
            try { await TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri("https://github.com/komabear/SONO-mixer"))!; }
            catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"launch link: {ex.Message}"); }
        };
        aboutSp.Children.Add(link);
        about.Child = aboutSp;

        sp.Children.Add(startupCard);
        sp.Children.Add(behaviorCard);
        sp.Children.Add(outputsCard);
        sp.Children.Add(appearanceCard);
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

    private static Control Section(string title) => new TextBlock
    {
        Text = title,
        Classes = { "muted" },
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
    };

    private static Control Row(string label, Control field)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(field);
        return sp;
    }
}
