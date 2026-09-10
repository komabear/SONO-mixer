using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
        Width = 460; Height = 560;
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
        var sp = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

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

        // ---- theme ----
        string themeId;
        lock (_vm.Settings) themeId = _vm.Settings.ThemeId;
        var themeBox = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var t in ThemeCatalog.All) themeBox.Items.Add(t.Name);
        var cur = ThemeCatalog.Get(themeId);
        int curIdx = 0;
        for (int i = 0; i < ThemeCatalog.All.Count; i++)
            if (ThemeCatalog.All[i].Id == cur.Id) { curIdx = i; break; }
        themeBox.SelectedIndex = curIdx;
        themeBox.SelectionChanged += (_, _) =>
        {
            if (themeBox.SelectedIndex < 0) return;
            var p = ThemeCatalog.All[themeBox.SelectedIndex];
            ThemeManager.Apply(p);
            lock (_vm.Settings) _vm.Settings.ThemeId = p.Id;
            _vm.Save();
            ThemeApplied?.Invoke();
            SONO.Core.Diagnostics.Log.Write($"theme applied: {p.Name}");
        };
        var themeRow = LabeledRow("Theme", themeBox);

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
        sp.Children.Add(themeRow);
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
