using System.Diagnostics;
using System.Text;

namespace Brembana;

/// <summary>
/// Logging file-based per tablet industriale (crash, errori, eventi kiosk).
/// </summary>
internal static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _logDirectory;

    public static string LogDirectory
    {
        get
        {
            if (_logDirectory != null)
                return _logDirectory;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Brembana", "Logs");
            Directory.CreateDirectory(dir);
            _logDirectory = dir;
            return dir;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.Append(ex);
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            var path = Path.Combine(LogDirectory, $"brembana-{DateTime.Now:yyyyMMdd}.log");
            lock (Sync)
            {
                File.AppendAllText(path, line);
            }

            Debug.WriteLine(line.TrimEnd());
        }
        catch
        {
            // Il logging non deve mai far crashare l'app.
        }
    }
}
