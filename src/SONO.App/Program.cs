using SONO.App;
using SONO.Core.Diagnostics;
using SONO.Core.Settings;
using SONO.Core.SystemIntegrations;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

internal static class Program
{
    private const string InstanceMutexName = @"Local\SONO.SingleInstance";
    private const string ShowSignalName = @"Local\SONO.ShowSignal";
    private const string ExitSignalName = @"Local\SONO.ExitSignal";

    [STAThread]
    private static void Main(string[] args)
    {
        bool autostartRequested = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)
                                  || args.Contains("/autostart", StringComparer.OrdinalIgnoreCase);
        bool exitRequested = args.Contains("--exit", StringComparer.OrdinalIgnoreCase)
                             || args.Contains("/exit", StringComparer.OrdinalIgnoreCase);

        // --exit: ask a running instance to quit cleanly, wait for it, and never start a new one
        if (exitRequested)
        {
            RequestRunningInstanceExit();
            return;
        }

        // single instance: Local\SONO.SingleInstance — second instance signals the first, then exits
        using var mutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        EventWaitHandle? showSignal = null;
        if (!firstInstance)
        {
            try
            {
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
                showSignal.Set();
            }
            catch (Exception ex) { Log.Write("show-signal: " + ex.Message); }
            return;
        }
        try
        {
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName, out bool createdNew);
        }
        catch { showSignal = null; }

        EventWaitHandle? exitSignal = null;
        try { exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName); }
        catch (Exception ex) { Log.Write("exit-signal: " + ex.Message); }

        // global exception logging — the mixer must survive and always leave a trace
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"FATAL unhandled: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        { Log.Write($"unobserved task: {e.Exception}"); e.SetObserved(); };

        // settings are read once here and handed to the App
        AppSettings? settings = null;
        try
        {
            settings = SettingsStore.Load();
            ConfigureAutostartOnce(settings);
        }
        catch (Exception ex) { Log.Write($"autostart configure: {ex.Message}"); }

        App.StartMinimized = autostartRequested;
        App.ShowSignal = showSignal;
        App.ExitSignal = exitSignal;
        App.InitialSettings = settings;

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

    /// <summary>"Starts with Windows by default": the first launch that finds the setting on
    /// and nothing configured yet writes the HKCU Run key — once. After that the in-app ⚙
    /// toggle is the only writer, so the entry is never re-pointed at another exe path on
    /// later launches. (This used to happen only on a --autostart launch, which nothing
    /// performs any more, so a fresh install never registered itself while the settings
    /// checkbox still read "on".) Debug builds skip it: a development run must not point
    /// the user's autostart at a bin\Debug exe.</summary>
    private static void ConfigureAutostartOnce(AppSettings s)
    {
#if !DEBUG
        if (s.AutostartConfigured) return;
        if (s.StartWithWindows && !Autostart.IsEnabled())
        {
            Autostart.Set(true);
            Log.Write("autostart Run key written (first launch)");
        }
        s.AutostartConfigured = true;
        SettingsStore.Save(s);
#endif
    }

    /// <summary>Signal the running instance (if any) to exit through its normal path — the
    /// one that restores every app volume/mute SONO changed — and wait until it is gone.
    /// Does nothing when SONO is not running or the running build predates this signal.</summary>
    private static void RequestRunningInstanceExit()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitSignalName, out var exit)) return;
            using (exit) exit.Set();

            // the first instance holds the mutex for its whole lifetime: once the name can
            // no longer be opened, that process has fully exited
            long deadline = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < deadline)
            {
                if (!Mutex.TryOpenExisting(InstanceMutexName, out var m)) return;
                m.Dispose();
                Thread.Sleep(100);
            }
            Log.Write("--exit: the running instance did not exit within 8s");
        }
        catch (Exception ex) { Log.Write("--exit: " + ex.Message); }
    }
}
