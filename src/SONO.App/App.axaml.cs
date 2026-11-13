using Avalonia;
using SONO.Core.Diagnostics;
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

    /// <summary>Set by `SONO.App.exe --exit`: the running instance should quit through its normal exit path.</summary>
    public static EventWaitHandle? ExitSignal { get; set; }

    /// <summary>Settings already loaded by Program (so the file is read once per launch).</summary>
    public static AppSettings? InitialSettings { get; set; }

    public override void Initialize()
    {
        // ColorView's theme is a compiled StyleInclude in App.axaml. It used to be parsed
        // from embedded XAML text here with the runtime XAML compiler, which cost about a
        // second of JIT before the first window could appear.
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = InitialSettings ?? SettingsStore.Load();
            // AllWithCustoms() resolves overrides from themes.json — ThemeCatalog.Get would
            // return the pristine entry and ignore the user's saved colors
            var themes = CustomThemeStore.AllWithCustoms();
            var startupTheme = themes.FirstOrDefault(t => t.Id == settings.ThemeId) ?? themes[0];
            ThemeManager.Apply(startupTheme);

            var engine = new AudioEngine();
            var hotkeys = Hotkeys ??= new HotkeyManager();
            var vm = new MixerVm(engine, hotkeys, settings);
            var win = new MainWindow(vm);

            // "Start minimized to tray" is about Windows starting SONO at sign-in (the Run
            // entry passes --autostart). Opening SONO yourself always shows the mixer —
            // otherwise a double-click would appear to do nothing.
            bool minimized;
            lock (settings) minimized = StartedByWindows && settings.StartMinimized;

            // The desktop lifetime shows desktop.MainWindow by itself as soon as startup
            // completes. So a window that must stay hidden is NOT registered as the main
            // window: it used to be registered and then hidden, which the lifetime undid
            // straight away — the setting never had any effect.
            if (!minimized)
            {
                desktop.MainWindow = win;
                win.Show();
            }

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
                            catch (Exception ex) { Log.Write("show-signal failed: " + ex.Message); }
                        });
                    }
                });
            }

            // `SONO.App.exe --exit` (used by the uninstaller) → leave through the normal exit
            // path, so every app volume/mute SONO changed is restored before the process goes
            if (ExitSignal is not null)
            {
                System.Threading.ThreadPool.RegisterWaitForSingleObject(ExitSignal, (_, _) =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            Log.Write("exit requested by another instance (--exit)");
                            win.ExitApp();
                        }
                        catch (Exception ex) { Log.Write("exit-signal failed: " + ex.Message); }
                    }), null, System.Threading.Timeout.Infinite, executeOnlyOnce: true);
            }

            desktop.Exit += (_, _) =>
            {
                try { vm.RebindHotkeys(); } catch (Exception ex) { Log.Write("exit: RebindHotkeys failed: " + ex.Message); }
                try { engine.Dispose(); } catch (Exception ex) { Log.Write("exit: engine dispose failed: " + ex.Message); }
                try { hotkeys.Dispose(); } catch (Exception ex) { Log.Write("exit: hotkeys dispose failed: " + ex.Message); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>True when this process was launched by the Windows Run entry (--autostart),
    /// as opposed to the user opening SONO.</summary>
    public static bool StartedByWindows { get; set; }
}
