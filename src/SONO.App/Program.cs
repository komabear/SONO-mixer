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
            // Someone else is running: signal its tray icon via a benign local message and exit.
            MessageBox.Show("SONO is already running. Look for its icon in the system tray.",
                "SONO", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = SONO.Core.Settings.SettingsStore.Load();
        var engine = new SONO.Core.Audio.AudioEngine();
        var hotkeys = new HotkeyManager();

        if (autostartRequested || settings.StartWithWindows)
        {
            try { if (!SONO.Core.SystemIntegrations.Autostart.IsEnabled()) SONO.Core.SystemIntegrations.Autostart.Set(true); }
            catch { /* non-fatal */ }
        }

        Application.Run(new MixerForm(engine, hotkeys, settings, autostartRequested));
        GC.KeepAlive(mutex);
    }
}
