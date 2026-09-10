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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            ThemeManager.Apply(ThemeCatalog.Get(settings.ThemeId));

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
