namespace Tommy.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger for troubleshooting UI/agent issues. Writes to
/// a daily file once <see cref="Initialize"/> is called; always echoes to the
/// debugger. Failures to write are swallowed so logging never breaks the app.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");
            Info("=== log start ===");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                if (_path is not null)
                    File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // never let logging throw
        }
    }
}
