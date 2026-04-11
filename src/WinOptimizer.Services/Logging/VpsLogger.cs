using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WinOptimizer.Services.Logging;

/// <summary>
/// Відправляє логи на VPS (http://91.236.195.98/api/logs) для віддаленого дебагу.
/// Працює асинхронно, не блокує основний потік.
/// Буферизує лінії і відправляє пачками кожні 3 секунди.
/// </summary>
public static class VpsLogger
{
    private const string VpsUrl = "http://91.236.195.98/api/logs";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly ConcurrentQueue<string> _buffer = new();
    private static readonly Timer _flushTimer;
    private static string _hwid = "unknown";
    private static string _pcName = "unknown";
    private static string _sessionId = "";
    private static bool _initialized;

    static VpsLogger()
    {
        // Флаш кожні 3 секунди
        _flushTimer = new Timer(FlushCallback, null, 3000, 3000);
    }

    /// <summary>
    /// Ініціалізувати з HWID і PC name (викликати один раз при старті).
    /// </summary>
    public static void Init(string hwid, string pcName)
    {
        _hwid = hwid ?? "unknown";
        _pcName = pcName ?? "unknown";
        _sessionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}";
        _initialized = true;

        // Відправити стартову лінію
        Log($"========== SESSION START: {_sessionId} ==========");
        Log($"HWID: {_hwid}, PC: {_pcName}");
        Log($"OS: {Environment.OSVersion}");
        Log($"User: {Environment.UserName}");
        Log($"ProcessId: {Environment.ProcessId}");
        Log($"WorkDir: {Environment.CurrentDirectory}");
        Log($"ExePath: {Environment.ProcessPath}");
    }

    /// <summary>
    /// Додати лінію логу в буфер для відправки на VPS.
    /// </summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        _buffer.Enqueue(line);

        // Якщо буфер занадто великий — примусовий flush
        if (_buffer.Count > 50)
        {
            _ = Task.Run(FlushAsync);
        }
    }

    /// <summary>
    /// Примусова відправка буферу (наприклад перед завершенням).
    /// </summary>
    public static async Task FlushAsync()
    {
        if (_buffer.IsEmpty) return;

        var lines = new List<string>();
        while (_buffer.TryDequeue(out var line))
        {
            lines.Add(line);
            if (lines.Count >= 100) break; // Макс 100 ліній за раз
        }

        if (lines.Count == 0) return;

        try
        {
            var payload = new
            {
                hwid = _hwid,
                pc_name = _pcName,
                session_id = _sessionId,
                lines = lines
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await Http.PostAsync(VpsUrl, content);
            // Не чекаємо на відповідь — fire and forget
        }
        catch
        {
            // Ігноруємо помилки мережі — логування не повинно крашити програму
            // Повертаємо лінії назад в буфер якщо менше 500
            if (_buffer.Count < 500)
            {
                foreach (var l in lines)
                    _buffer.Enqueue(l);
            }
        }
    }

    private static void FlushCallback(object? state)
    {
        if (_buffer.IsEmpty) return;
        _ = Task.Run(FlushAsync);
    }

    /// <summary>
    /// Зупинити логер і відправити залишок.
    /// </summary>
    public static async Task ShutdownAsync()
    {
        Log("========== SESSION END ==========");
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        await FlushAsync();
    }
}
