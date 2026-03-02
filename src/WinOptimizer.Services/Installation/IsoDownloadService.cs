using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Installation;

/// <summary>
/// Завантаження Windows ISO з VPS сервера з прогресом.
/// 1. Запитує /api/iso/info — отримує filename та size
/// 2. Качає напряму через nginx /iso/{filename} — ефективний sendfile
/// ISO зберігається в C:\ProgramData\WinOptimizer\ISO\
/// </summary>
public static class IsoDownloadService
{
    private const string VpsBaseUrl = "http://84.238.132.84";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromHours(2) // ISO файли великі, тайм-аут 2 години
    };

    private static readonly string IsoDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "ISO");

    private static void DLog(string msg)
    {
        Logger.Info($"[ISO] {msg}");
    }

    /// <summary>
    /// Додати папку ISO до виключень Windows Defender (щоб не блокував .tmp файл).
    /// </summary>
    private static void AddDefenderExclusion(string path)
    {
        try
        {
            DLog($"Adding Defender exclusion for: {path}");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{path}' -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            DLog($"Defender exclusion added (exit={proc?.ExitCode})");
        }
        catch (Exception ex)
        {
            DLog($"Defender exclusion failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Видалити старий .tmp файл (може бути заблокований з минулого запуску).
    /// </summary>
    private static void CleanupTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                DLog($"Cleaning up old temp file: {tempPath}");
                File.Delete(tempPath);
                DLog("Old temp file deleted");
            }
        }
        catch (Exception ex)
        {
            DLog($"Cannot delete old temp: {ex.Message}");
            // Спробуємо з іншим ім'ям
        }
    }

    /// <summary>
    /// Завантажити Windows ISO з VPS.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string windowsVersion,
        string language,
        Action<long, long, double>? onProgress = null,
        Action<string>? onDetail = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(IsoDirectory);

        // Додати виключення Defender для папки ISO ПЕРЕД завантаженням
        AddDefenderExclusion(IsoDirectory);

        DLog($"Starting ISO download: version={windowsVersion}, lang={language}");
        onDetail?.Invoke("З'єднання з сервером...");

        // Step 1: Запитати API про доступний ISO
        string downloadUrl;
        string isoFileName;
        long expectedSize = 0;

        try
        {
            var infoUrl = $"{VpsBaseUrl}/api/iso/info?version={windowsVersion}&lang={language}";
            DLog($"Querying ISO info: {infoUrl}");

            var infoResponse = await Http.GetStringAsync(infoUrl, ct);
            using var doc = JsonDocument.Parse(infoResponse);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
                throw new Exception($"ISO not available: {error}");
            }

            isoFileName = root.GetProperty("filename").GetString() ?? $"Windows{windowsVersion}_{language}.iso";
            var relativeUrl = root.GetProperty("download_url").GetString() ?? $"/iso/{isoFileName}";
            expectedSize = root.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;

            // Прямий URL через nginx
            downloadUrl = $"{VpsBaseUrl}{relativeUrl}";
            DLog($"ISO info: file={isoFileName}, size={expectedSize / 1024 / 1024} MB, url={downloadUrl}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DLog($"ISO info request failed: {ex.Message}");
            isoFileName = $"Windows{windowsVersion}_{language}.iso";
            downloadUrl = $"{VpsBaseUrl}/api/iso/download?version={windowsVersion}&lang={language}";
            DLog($"Fallback URL: {downloadUrl}");
        }

        var isoPath = Path.Combine(IsoDirectory, isoFileName);
        DLog($"Target path: {isoPath}");

        // Якщо ISO вже завантажено — перевірити розмір
        if (File.Exists(isoPath))
        {
            var existingSize = new FileInfo(isoPath).Length;
            if (existingSize > 1_000_000_000) // > 1GB
            {
                if (expectedSize > 0 && existingSize == expectedSize)
                {
                    DLog($"ISO already exists and size matches: {existingSize / 1024 / 1024} MB, skipping download");
                    onDetail?.Invoke($"ISO вже завантажено ({existingSize / 1024 / 1024} MB)");
                    return isoPath;
                }
                else if (expectedSize == 0)
                {
                    DLog($"ISO already exists: {existingSize / 1024 / 1024} MB, skipping download");
                    onDetail?.Invoke($"ISO вже завантажено ({existingSize / 1024 / 1024} MB)");
                    return isoPath;
                }
                else
                {
                    DLog($"ISO exists but size mismatch ({existingSize} vs expected {expectedSize}), re-downloading");
                    try { File.Delete(isoPath); } catch { }
                }
            }
            else
            {
                DLog($"ISO exists but too small ({existingSize} bytes), re-downloading");
                try { File.Delete(isoPath); } catch { }
            }
        }

        // Видалити старий .tmp файли
        CleanupTempFile(isoPath + ".tmp");
        CleanupTempFile(isoPath + ".downloading");

        // Step 2: Завантаження ISO — пишемо ОДРАЗУ у фінальний файл (без .tmp → rename!)
        // Причина: File.Move фейлив бо FileStream не закривався вчасно + Defender тримав хендл
        // Тепер: пишемо одразу в .iso, якщо download фейлить — видаляємо неповний файл
        DLog($"Download URL: {downloadUrl}");
        onDetail?.Invoke($"Завантаження Windows {windowsVersion} ISO...");

        // Видалити старий ISO (якщо не пройшов перевірку розміру вище)
        if (File.Exists(isoPath))
        {
            try { File.Delete(isoPath); DLog("Deleted old incomplete ISO"); }
            catch (Exception ex) { DLog($"Cannot delete old ISO: {ex.Message}"); }
        }

        // Retry logic — до 3 спроб
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                DLog($"Download attempt {attempt}/{maxRetries}");

                using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = "";
                    try { errorBody = await response.Content.ReadAsStringAsync(ct); } catch { }
                    DLog($"Download failed: HTTP {(int)response.StatusCode} — {errorBody}");
                    throw new Exception($"ISO download failed: HTTP {(int)response.StatusCode}");
                }

                var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
                DLog($"Content-Length: {totalBytes} bytes ({totalBytes / 1024 / 1024} MB)");

                if (totalBytes > 0 && totalBytes < 1_000_000)
                {
                    var body = "";
                    try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
                    DLog($"Response too small, likely error: {body}");
                    throw new Exception($"ISO not available on server for Windows {windowsVersion} ({language})");
                }

                // Завантаження з прогресом — пишемо ОДРАЗУ у фінальний .iso файл
                var startTime = DateTime.Now;
                long downloaded = 0;

                // Блок-скоп для FileStream — гарантовано закриється після download
                {
                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(isoPath,
                        FileMode.Create, FileAccess.Write,
                        FileShare.ReadWrite, // Дозволяємо Defender читати під час запису
                        bufferSize: 1024 * 1024);

                    var buffer = new byte[1024 * 1024]; // 1MB chunks
                    int bytesRead;
                    var lastProgressReport = DateTime.Now;
                    var lastLogReport = DateTime.Now;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;

                        // Звіт про прогрес кожні 500ms (для UI)
                        if ((DateTime.Now - lastProgressReport).TotalMilliseconds > 500)
                        {
                            lastProgressReport = DateTime.Now;
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            var speedMBps = elapsed > 0 ? (downloaded / 1024.0 / 1024.0) / elapsed : 0;

                            onProgress?.Invoke(downloaded, totalBytes, speedMBps);

                            var downloadedMB = downloaded / 1024 / 1024;
                            var totalMB = totalBytes / 1024 / 1024;
                            var percent = totalBytes > 0 ? (int)(downloaded * 100 / totalBytes) : 0;
                            onDetail?.Invoke($"Завантаження: {downloadedMB} / {totalMB} MB ({percent}%) — {speedMBps:F1} MB/s");
                        }

                        // Лог прогресу кожні 60 секунд (для VPS debug)
                        if ((DateTime.Now - lastLogReport).TotalSeconds > 60)
                        {
                            lastLogReport = DateTime.Now;
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            var speedMBps = elapsed > 0 ? (downloaded / 1024.0 / 1024.0) / elapsed : 0;
                            var percent = totalBytes > 0 ? (int)(downloaded * 100 / totalBytes) : 0;
                            DLog($"Download progress: {downloaded / 1024 / 1024}/{totalBytes / 1024 / 1024} MB ({percent}%) speed={speedMBps:F1} MB/s");
                        }
                    }

                    await fileStream.FlushAsync(ct);
                    // fileStream закривається тут (кінець блоку)
                }

                DLog($"Download complete, {downloaded} bytes written directly to: {isoPath}");

                // Перевірка розміру
                if (expectedSize > 0)
                {
                    var actualSize = new FileInfo(isoPath).Length;
                    if (actualSize != expectedSize)
                    {
                        DLog($"Size mismatch! Expected {expectedSize}, got {actualSize}. Deleting and retrying...");
                        try { File.Delete(isoPath); } catch { }
                        throw new Exception($"ISO size mismatch: expected {expectedSize}, got {actualSize}");
                    }
                    DLog($"Size verified: {actualSize} bytes OK");
                }

                var totalElapsed = (DateTime.Now - startTime).TotalSeconds;
                var finalSpeedMBps = totalElapsed > 0 ? (downloaded / 1024.0 / 1024.0) / totalElapsed : 0;
                DLog($"ISO download complete: {downloaded / 1024 / 1024} MB in {totalElapsed:F0}s ({finalSpeedMBps:F1} MB/s)");
                onDetail?.Invoke($"ISO завантажено: {downloaded / 1024 / 1024} MB");

                return isoPath;
            }
            catch (OperationCanceledException)
            {
                DLog("ISO download cancelled");
                // Видалити неповний файл
                try { if (File.Exists(isoPath)) File.Delete(isoPath); } catch { }
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DLog($"ISO download error (attempt {attempt}): {ex.GetType().Name}: {ex.Message}");

                // Видалити неповний файл
                try { if (File.Exists(isoPath)) File.Delete(isoPath); } catch { }

                if (attempt < maxRetries)
                {
                    DLog($"Retrying in 5 seconds...");
                    onDetail?.Invoke($"Помилка завантаження, повторна спроба {attempt + 1}/{maxRetries}...");
                    await Task.Delay(5000, ct);
                }
                else
                {
                    throw new Exception($"Failed to download Windows {windowsVersion} ISO after {maxRetries} attempts: {ex.Message}", ex);
                }
            }
        }

        throw new Exception("ISO download failed: unexpected state");
    }

    /// <summary>
    /// Видалити завантажений ISO файл (після успішної установки).
    /// </summary>
    public static void CleanupIso(string isoPath)
    {
        try
        {
            if (File.Exists(isoPath))
            {
                File.Delete(isoPath);
                DLog($"ISO deleted: {isoPath}");
            }
        }
        catch (Exception ex)
        {
            DLog($"Failed to delete ISO: {ex.Message}");
        }
    }

    /// <summary>
    /// Очистити всю папку ISO.
    /// </summary>
    public static void CleanupAll()
    {
        try
        {
            if (Directory.Exists(IsoDirectory))
            {
                Directory.Delete(IsoDirectory, recursive: true);
                DLog("ISO directory cleaned up");
            }
        }
        catch (Exception ex)
        {
            DLog($"Failed to cleanup ISO directory: {ex.Message}");
        }
    }
}
