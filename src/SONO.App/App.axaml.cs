using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SONO.App.Themes;
using SONO.App.ViewModels;
using SONO.Core.Audio;
using SONO.Core.Settings;

namespace SONO.App;

public class App : Application
{
    /// <summary>Global hotkey manager (set in Program before MainWindow is built; HotkeyBox uses it).</summary>
    public static HotkeyManager? Hotkeys { get; set; }

    /// <summary>Second-instance signaler: set when another SONO process wants the first to show itself.</summary>
    public static EventWaitHandle? ShowSignal { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ColorView templates live in the ColorPicker assembly; include its Fluent theme at
        // runtime (the XAML compiler can't resolve the avres:// URI at build time)
        try
        {
            // ColorView's template lives in the ColorPicker assembly as ResourceDictionaries.
            // Each leaf parses to a ResourceDictionary (resources + nested ControlThemes) that
            // must go into Resources.MergedDictionaries for the {TemplateBinding} lookups to resolve.
            var colorPickerAsm = typeof(Avalonia.Controls.ColorView).Assembly;
            string[] leaves =
            {
                "Avalonia.Controls.ColorPicker.Themes.Fluent.ColorView.xaml",       // defines shared converters
                "Avalonia.Controls.ColorPicker.Themes.Fluent.ColorSpectrum.xaml",
                "Avalonia.Controls.ColorPicker.Themes.Fluent.ColorSlider.xaml",
                "Avalonia.Controls.ColorPicker.Themes.Fluent.ColorPreviewer.xaml",
            };
            foreach (var resName in leaves)
            {
                using var stream = colorPickerAsm.GetManifestResourceStream(resName)
                    ?? throw new InvalidOperationException($"{resName} missing");
                using var sr = new System.IO.StreamReader(stream);
                if (AvaloniaRuntimeXamlLoader.Parse(sr.ReadToEnd()) is Avalonia.Controls.ResourceDictionary rd)
                    Resources.MergedDictionaries.Add(rd);
            }
            // The aggregate Fluent.xaml DEFINES shared resources (EnumToBoolConverter etc.) the
            // templates use via StaticResource, but its own MergeResourceInclude nodes point at
            // avares URIs the runtime parser can't resolve. Parse it with those nodes stripped.
            using var agg = colorPickerAsm.GetManifestResourceStream("Avalonia.Controls.ColorPicker.Themes.Fluent.Fluent.xaml");
            using var aggSr = new System.IO.StreamReader(agg);
            var aggText = aggSr.ReadToEnd();
            aggText = System.Text.RegularExpressions.Regex.Replace(
                aggText,
                @"\s*<MergeResourceInclude[^>]*/>",
                "");
            var aggParsed = AvaloniaRuntimeXamlLoader.Parse(aggText);
            SONO.Core.Diagnostics.Log.Write($"colorpicker: aggregate parsed type={aggParsed?.GetType().FullName}");
            switch (aggParsed)
            {
                case Avalonia.Styling.Styles styles:
                    Styles.Add(styles);
                    break;
                case Avalonia.Controls.ResourceDictionary rd2:
                    Resources.MergedDictionaries.Add(rd2);
                    break;
            }
            // the aggregate also defines shared resources (EnumToBoolConverter etc.) referenced
            // by the leaf templates — register its resource entries without re-resolving its includes
            // (converters + shared keys live in ColorView.xaml, loaded last above)
        }
        catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"colorpicker theme: {ex.Message}"); }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            // AllWithCustoms() resolves overrides from themes.json — ThemeCatalog.Get would
            // return the pristine entry and ignore the user's saved colors
            var startupTheme = CustomThemeStore.AllWithCustoms().FirstOrDefault(t => t.Id == settings.ThemeId)
                               ?? CustomThemeStore.AllWithCustoms()[0];
            ThemeManager.Apply(startupTheme);

            var engine = new AudioEngine();
            var hotkeys = Hotkeys ??= new HotkeyManager();
            var vm = new MixerVm(engine, hotkeys, settings);
            var win = new MainWindow(vm);
            desktop.MainWindow = win;

            bool minimized;
            lock (settings) minimized = StartMinimized || settings.StartMinimized;
            if (minimized) win.Hide();
            else win.Show();

            // second instance → bring to front
            if (ShowSignal is not null)
            {
                var signal = ShowSignal;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (signal.WaitOne())
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try { win.Show(); win.WindowState = WindowState.Normal; win.Activate(); }
                            catch { }
                        });
                    }
                });
            }

            desktop.Exit += (_, _) =>
            {
                try { vm.RebindHotkeys(); } catch { }
                try { engine.Dispose(); } catch { }
                try { hotkeys.Dispose(); } catch { }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static bool StartMinimized { get; set; }
}
