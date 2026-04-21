using System.IO;
using System.Text;

namespace QuickFind.Core;

// Minimal file-based logger — no external deps, thread-safe, rolling per day.
// Output: %LOCALAPPDATA%\QuickFind\logs\quickfind-YYYY-MM-DD.log
// Keeps at most 7 daily files on disk.
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _logDir;
    private static bool _rollChecked;
    private static DateTime _lastRollDate = DateTime.MinValue;

    static Logger()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickFind", "logs");
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static string LogDirectory => _logDir;

    public static string? CurrentLogFile
    {
        get
        {
            lock (_lock)
            {
                try { return GetLogPath(); } catch { return null; }
            }
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                EnsureDir();
                RollIfNeeded();

                var sb = new StringBuilder(256);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(level).Append("] ");
                sb.Append(message);
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.Append("    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                        sb.AppendLine().Append(ex.StackTrace);
                }
                sb.AppendLine();

                File.AppendAllText(GetLogPath(), sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logger must never throw. If disk is full or path is bad,
            // we silently drop the line rather than cascade a new failure.
        }
    }

    private static void EnsureDir()
    {
        if (!Directory.Exists(_logDir))
            Directory.CreateDirectory(_logDir);
    }

    private static string GetLogPath()
    {
        return Path.Combine(_logDir, $"quickfind-{DateTime.Now:yyyy-MM-dd}.log");
    }

    private static void RollIfNeeded()
    {
        var today = DateTime.Now.Date;
        if (_rollChecked && _lastRollDate == today) return;
        _lastRollDate = today;
        _rollChecked = true;

        // Delete log files older than 7 days.
        try
        {
            var cutoff = today.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(_logDir, "quickfind-*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    try { info.Delete(); } catch { }
                }
            }
        }
        catch { }
    }
}
