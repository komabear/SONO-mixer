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

        if (autostartRequested || settings.StartWithWindows)
        {
            try { if (!Core.SystemIntegrations.Autostart.IsEnabled()) Core.SystemIntegrations.Autostart.Set(true); }
            catch { /* non-fatal */ }
        }

        // ApplicationContext lets us hot-swap the main form (theme changes) while the
        // audio engine, routing and hotkeys keep running.
        Application.Run(new SonoContext(engine, hotkeys, settings, autostartRequested));
        GC.KeepAlive(mutex);
    }
}

internal sealed class SonoContext : ApplicationContext
{
    private readonly AudioEngine _engine;
    private readonly HotkeyManager _hotkeys;
    private readonly AppSettings _settings;
    private readonly bool _boot;

    public SonoContext(AudioEngine engine, HotkeyManager hotkeys, AppSettings settings, bool boot)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _settings = settings;
        _boot = boot;
        ShowMain();
    }

    private void ShowMain()
    {
        var form = new MixerForm(_engine, _hotkeys, _settings, _boot);
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
