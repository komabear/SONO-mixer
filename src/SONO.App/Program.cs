using SONO.Core.Audio;
using SONO.Core.Settings;

namespace SONO.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // survive UI/background-thread exceptions: log them, keep the mixer alive
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            SONO.Core.Diagnostics.Log.Write($"UI thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SONO.Core.Diagnostics.Log.Write($"FATAL unhandled: {e.ExceptionObject}");

        bool autostartRequested = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)
                                  || args.Contains("/autostart", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, @"Local\SONO.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("SONO is already running. Look for its icon in the system tray.",
                "SONO", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = SettingsStore.Load();
        Theme.Apply(ThemeCatalog.Get(settings.ThemeId));
        var engine = new AudioEngine();
        var hotkeys = new HotkeyManager();
        var routing = new RoutingEngine();   // ONE instance for the whole process —
                                             // per-form engines used to stack on theme swaps
                                             // (double capture ≈ +6 dB + phasey audio)

        // autostart: write the Run key ONCE, on the very first launch of a given exe.
        // Re-asserting on every launch (the old behavior) stomped the user's "off" choice
        // AND re-pointed the entry whenever the app was launched from a different path
        // (e.g. a dev build), leaving a stale entry after uninstall.
        // After that, the in-app ⚙ toggle (settings.StartWithWindows) is the only writer.
        if (autostartRequested || (settings.StartWithWindows && !settings.AutostartConfigured))
        {
            try
            {
                if (!Core.SystemIntegrations.Autostart.IsEnabled())
                    Core.SystemIntegrations.Autostart.Set(true);
                settings.AutostartConfigured = true;
                SettingsStore.Save(settings);
            }
            catch { /* non-fatal */ }
        }

        // ApplicationContext lets us hot-swap the main form (theme changes) while the
        // audio engine, routing and hotkeys keep running.
        Application.Run(new SonoContext(engine, hotkeys, routing, settings, autostartRequested));
        GC.KeepAlive(mutex);
    }
}

internal sealed class SonoContext : ApplicationContext
{
    private readonly AudioEngine _engine;
    private readonly HotkeyManager _hotkeys;
    private readonly RoutingEngine _routing;
    private readonly AppSettings _settings;
    private readonly bool _boot;

    public SonoContext(AudioEngine engine, HotkeyManager hotkeys, RoutingEngine routing,
        AppSettings settings, bool boot)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _routing = routing;
        _settings = settings;
        _boot = boot;
        ShowMain();
    }

    private void ShowMain()
    {
        var form = new MixerForm(_engine, _hotkeys, _routing, _settings, _boot);
        if (_hasBounds)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = _bounds;
            form.WindowState = _state;
        }
        form.UiRestartRequested += () =>
        {
            _bounds = form.Bounds;          // seamless swap: keep size/position/state
            _state = form.WindowState;
            _hasBounds = true;
            form.ForceClose();
        };
        form.FormClosed += (_, _) => { if (!form.SuppressCleanup) ExitThread(); else ShowMain(); };
        form.Show();
    }

    private Rectangle _bounds;
    private FormWindowState _state;
    private bool _hasBounds;
}
