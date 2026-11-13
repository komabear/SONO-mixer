namespace SONO.Core.Diagnostics;

/// <summary>Simple append-only log for diagnosing "it silently died" reports.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SONO");
    private static string Path => System.IO.Path.Combine(Dir, "log.txt");
    private static string OldPath => System.IO.Path.Combine(Dir, "log.old.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                // at 512 KB the log rolls over to log.old.txt (one generation kept). It used
                // to be deleted outright, so whatever led up to a problem was gone.
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 512 * 1024) File.Move(Path, OldPath, overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\r\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
