using SONO.App;
using SONO.Core.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool autostartRequested = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)
                                  || args.Contains("/autostart", StringComparer.OrdinalIgnoreCase);

        // single instance: Local\SONO.SingleInstance — second instance signals the first, then exits
        using var mutex = new Mutex(true, @"Local\SONO.SingleInstance", out bool firstInstance);
        EventWaitHandle? showSignal = null;
        if (!firstInstance)
        {
            try
            {
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SONO.ShowSignal");
                showSignal.Set();
            }
            catch { }
            return;
        }
        try
        {
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SONO.ShowSignal", out bool createdNew);
        }
        catch { showSignal = null; }

        // global exception logging — the mixer must survive and always leave a trace
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"FATAL unhandled: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        { Log.Write($"unobserved task: {e.Exception}"); e.SetObserved(); };

        // once-ever autostart Run-key write (first --autostart launch only; after that the
        // in-app ⚙ toggle is the only writer — never re-point the entry on later launches)
        try
        {
            if (autostartRequested && !SONO.Core.SystemIntegrations.Autostart.IsEnabled())
            {
                var s = SONO.Core.Settings.SettingsStore.Load();
                if (!s.AutostartConfigured)
                {
                    SONO.Core.SystemIntegrations.Autostart.Set(true);
                    s.AutostartConfigured = true;
                    SONO.Core.Settings.SettingsStore.Save(s);
                    Log.Write("autostart Run key written (first --autostart launch)");
                }
            }
        }
        catch (Exception ex) { Log.Write($"autostart configure: {ex.Message}"); }

        App.StartMinimized = autostartRequested;
        App.ShowSignal = showSignal;

        Log.Write($"SONO starting (autostart={autostartRequested}, firstInstance={firstInstance})");

        try
        {
            App.Hotkeys = new HotkeyManager();
            Avalonia.AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Write($"startup fatal: {ex}");
            throw;
        }
        Log.Write("SONO exited cleanly");
    }
}
