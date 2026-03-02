namespace WinOptimizer.Services.Logging;

public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "WinOptimizer.log");

    // Додатковий лог поруч з exe для SMB доступу
    private static readonly string ExeDirLog = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "WinOptimizer_Debug.log");

    private static string? _networkLogPath;
    private static readonly object _lock = new();

    public static void SetNetworkLogPath(string path)
    {
        _networkLogPath = path;
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
            if (_networkLogPath != null)
            {
                try { File.AppendAllText(_networkLogPath, line + Environment.NewLine); } catch { }
            }
        }

        // Відправити на VPS (асинхронно, не блокує)
        try { VpsLogger.Log($"[{level}] {message}"); } catch { }
    }
}
