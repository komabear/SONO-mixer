namespace SONO.Core.Diagnostics;

/// <summary>Simple append-only log for diagnosing "it silently died" reports.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SONO", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (new FileInfo(Path).Exists && new FileInfo(Path).Length > 512 * 1024) File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\r\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
