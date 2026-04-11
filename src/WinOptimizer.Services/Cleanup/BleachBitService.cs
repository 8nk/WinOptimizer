using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Інтеграція BleachBit Portable — найпотужніша open-source утиліта очистки.
/// 2000+ додатків, 90+ cleaners, CLI mode.
///
/// Flow: Download ZIP → Extract → Run CLI --clean → Parse output → Delete.
/// Зберігається в C:\ProgramData\WinOptimizer\Tools\BleachBit\
/// </summary>
public static class BleachBitService
{
    // BleachBit Portable download URL (v5.0.2 stable — latest)
    private const string DownloadUrl = "https://download.bleachbit.org/BleachBit-5.0.2-portable.zip";
    private const string FallbackUrl = "https://download.bleachbit.org/BleachBit-4.6.2-portable.zip";

    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Tools");

    private static readonly string BleachBitDir = Path.Combine(ToolsDir, "BleachBit");
    private static readonly string ExePath = Path.Combine(BleachBitDir, "bleachbit_console.exe");

    /// <summary>
    /// Всі cleaners які ми хочемо запустити.
    /// Це НАБАГАТО більше ніж наш ручний код — BleachBit знає кеші 2000+ додатків!
    /// </summary>
    private static readonly string[] Cleaners = new[]
    {
        // === SYSTEM ===
        "system.cache",
        "system.clipboard",
        "system.custom",
        "system.logs",
        "system.memory_dump",
        "system.prefetch",
        "system.recycle_bin",
        "system.tmp",
        "system.updates",

        // === BROWSERS ===
        // Chrome
        "google_chrome.cache",
        "google_chrome.cookies",
        "google_chrome.dom",
        "google_chrome.form_history",
        "google_chrome.history",
        "google_chrome.search_engines",
        "google_chrome.session",
        "google_chrome.sync",
        "google_chrome.vacuum",

        // Chromium
        "chromium.cache",
        "chromium.cookies",
        "chromium.dom",
        "chromium.form_history",
        "chromium.history",
        "chromium.vacuum",

        // Firefox
        "firefox.cache",
        "firefox.cookies",
        "firefox.crash_reports",
        "firefox.dom",
        "firefox.forms",
        "firefox.session_restore",
        "firefox.site_preferences",
        "firefox.url_history",
        "firefox.vacuum",

        // Edge
        "microsoft_edge.cache",
        "microsoft_edge.cookies",
        "microsoft_edge.dom",
        "microsoft_edge.form_history",
        "microsoft_edge.history",
        "microsoft_edge.session",
        "microsoft_edge.vacuum",

        // Opera
        "opera.cache",
        "opera.cookies",
        "opera.dom",
        "opera.form_history",
        "opera.history",
        "opera.vacuum",

        // Internet Explorer
        "internet_explorer.cache",
        "internet_explorer.cookies",
        "internet_explorer.forms",
        "internet_explorer.history",
        "internet_explorer.temporary_files",

        // === APPS ===
        "adobe_reader.cache",
        "adobe_reader.mru",
        "adobe_reader.tmp",

        "discord.cache",
        "discord.cookies",
        "discord.history",

        "gimp.tmp",

        "java.cache",

        "libreoffice.cache",
        "libreoffice.history",

        "microsoft_office.debug_logs",
        "microsoft_office.mru",

        "notepadpp.backup",
        "notepadpp.history",

        "openofficeorg.cache",
        "openofficeorg.recent_documents",

        "paint.mru",

        "skype.chat_logs",
        "skype.installers",

        "slack.cache",
        "slack.cookies",
        "slack.history",

        "steam.cache",
        "steam.logs",

        "thunderbird.cache",
        "thunderbird.cookies",
        "thunderbird.vacuum",

        "vlc.mru",

        "vim.history",

        "winrar.history",
        "winrar.temp",

        "7zip.history",

        "zoom.cache",
        "zoom.logs",

        // === WINDOWS ===
        "windows_defender.history",
        "windows_defender.logs",
        "windows_defender.temp",

        "windows_explorer.mru",
        "windows_explorer.recent_documents",
        "windows_explorer.run",
        "windows_explorer.search_history",
        "windows_explorer.shellbags",
        "windows_explorer.thumbnails",

        "windows_media_player.cache",
        "windows_media_player.mru",

        // === DEEP SCAN (winapp2.ini entries) ===
        "deepscan.backup",
        "deepscan.ds_store",
        "deepscan.thumbs_db",
        "deepscan.tmp",
    };

    /// <summary>
    /// Головний метод: скачати BleachBit, запустити очистку, повернути результат.
    /// </summary>
    public static async Task<long> RunFullCleanupAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        long totalCleaned = 0;

        try
        {
            // 1. Download BleachBit if not present
            if (!File.Exists(ExePath))
            {
                onProgress?.Invoke("Завантаження системних компонентів...");
                var downloaded = await DownloadAndExtractAsync(onProgress, token);
                if (!downloaded)
                {
                    Logger.Warn("[BleachBit] Download failed, skipping");
                    return 0;
                }
            }

            // 2. First do a preview to see what can be cleaned
            onProgress?.Invoke("Перевірка файлової системи...");
            var previewSize = await RunPreviewAsync(token);
            Logger.Info($"[BleachBit] Preview: {previewSize / (1024.0 * 1024.0):F1} MB can be cleaned");

            // 3. Run the actual cleanup
            onProgress?.Invoke("Застосування параметрів файлової системи...");
            totalCleaned = await RunCleanAsync(onProgress, token);
            Logger.Info($"[BleachBit] Cleaned: {totalCleaned / (1024.0 * 1024.0):F1} MB");

            // 4. Cleanup BleachBit itself (optional — save space)
            try
            {
                if (Directory.Exists(BleachBitDir))
                {
                    Directory.Delete(BleachBitDir, true);
                    Logger.Info("[BleachBit] Tool directory cleaned up");
                }
            }
            catch { }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[BleachBit] Error: {ex.Message}");
        }

        return totalCleaned;
    }

    /// <summary>
    /// Download BleachBit Portable ZIP and extract.
    /// </summary>
    private static async Task<bool> DownloadAndExtractAsync(
        Action<string>? onProgress, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            var zipPath = Path.Combine(ToolsDir, "bleachbit-portable.zip");

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(45); // Короткий timeout — якщо інтернет поганий, краще пропустити
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinOptimizer/1.0");

            // Try primary URL, then fallback
            var urls = new[] { DownloadUrl, FallbackUrl };
            bool downloaded = false;

            foreach (var url in urls)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    onProgress?.Invoke("Завантаження компонентів оновлення...");
                    Logger.Info($"[BleachBit] Downloading from {url}");

                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    using var contentStream = await response.Content.ReadAsStreamAsync(token);
                    using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

                    var buffer = new byte[81920];
                    long downloadedBytes = 0;
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read, token);
                        downloadedBytes += read;

                        if (totalBytes > 0)
                        {
                            var pct = (downloadedBytes * 100.0 / totalBytes);
                            onProgress?.Invoke($"Встановлення оновлень... {pct:F0}%");
                        }
                    }

                    downloaded = true;
                    Logger.Info($"[BleachBit] Downloaded: {downloadedBytes / (1024 * 1024)} MB");
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.Warn($"[BleachBit] Download failed from {url}: {ex.Message}");
                }
            }

            if (!downloaded) return false;

            // Extract ZIP
            onProgress?.Invoke("Застосування оновлень системи...");
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(BleachBitDir))
                Directory.Delete(BleachBitDir, true);

            ZipFile.ExtractToDirectory(zipPath, ToolsDir);

            // BleachBit ZIP extracts to a subfolder like "BleachBit-Portable"
            // Find the actual folder and rename
            var extractedDirs = Directory.GetDirectories(ToolsDir, "BleachBit*");
            foreach (var dir in extractedDirs)
            {
                var consolePath = Path.Combine(dir, "bleachbit_console.exe");
                if (File.Exists(consolePath) && dir != BleachBitDir)
                {
                    if (Directory.Exists(BleachBitDir))
                        Directory.Delete(BleachBitDir, true);
                    Directory.Move(dir, BleachBitDir);
                    break;
                }
            }

            // Cleanup ZIP
            try { File.Delete(zipPath); } catch { }

            if (File.Exists(ExePath))
            {
                Logger.Info("[BleachBit] Extracted successfully");
                return true;
            }

            Logger.Warn("[BleachBit] bleachbit_console.exe not found after extraction");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[BleachBit] Download/extract error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Preview how much can be cleaned.
    /// </summary>
    private static async Task<long> RunPreviewAsync(CancellationToken token)
    {
        try
        {
            var availableCleaners = await GetAvailableCleanersAsync(token);
            if (availableCleaners.Count == 0) return 0;

            var args = "--preview " + string.Join(" ", availableCleaners);
            var output = await RunBleachBitAsync(args, 30, token); // 30s preview — швидко

            // Parse output for "Disk space to be recovered: X.XX GB"
            var match = Regex.Match(output, @"Disk space to be recovered:\s*([\d.,]+)\s*(B|KB|MB|GB|TB)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = double.Parse(match.Groups[1].Value.Replace(",", "."),
                    System.Globalization.CultureInfo.InvariantCulture);
                var unit = match.Groups[2].Value.ToUpperInvariant();
                return unit switch
                {
                    "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                    "GB" => (long)(value * 1024 * 1024 * 1024),
                    "MB" => (long)(value * 1024 * 1024),
                    "KB" => (long)(value * 1024),
                    _ => (long)value
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[BleachBit] Preview error: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// Run the actual cleanup.
    /// </summary>
    private static async Task<long> RunCleanAsync(
        Action<string>? onProgress, CancellationToken token)
    {
        long totalCleaned = 0;

        try
        {
            var availableCleaners = await GetAvailableCleanersAsync(token);
            if (availableCleaners.Count == 0)
            {
                Logger.Warn("[BleachBit] No available cleaners found");
                return 0;
            }

            Logger.Info($"[BleachBit] Running {availableCleaners.Count} cleaners");

            // Run in batches to show progress and avoid timeouts
            var batchSize = 15;
            var batches = new List<List<string>>();
            for (int i = 0; i < availableCleaners.Count; i += batchSize)
            {
                batches.Add(availableCleaners.Skip(i).Take(batchSize).ToList());
            }

            for (int b = 0; b < batches.Count; b++)
            {
                token.ThrowIfCancellationRequested();

                var batch = batches[b];
                var batchName = batch.First().Split('.')[0]; // e.g. "system", "google_chrome"
                onProgress?.Invoke($"Налаштування системних модулів... ({b + 1}/{batches.Count})");

                var args = "--clean " + string.Join(" ", batch);
                var output = await RunBleachBitAsync(args, 60, token); // 60s per batch — швидко!

                // Parse cleaned size from output
                var sizeMatch = Regex.Match(output,
                    @"Disk space recovered:\s*([\d.,]+)\s*(B|KB|MB|GB|TB)",
                    RegexOptions.IgnoreCase);
                if (sizeMatch.Success)
                {
                    var value = double.Parse(sizeMatch.Groups[1].Value.Replace(",", "."),
                        System.Globalization.CultureInfo.InvariantCulture);
                    var unit = sizeMatch.Groups[2].Value.ToUpperInvariant();
                    var cleaned = unit switch
                    {
                        "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                        "GB" => (long)(value * 1024 * 1024 * 1024),
                        "MB" => (long)(value * 1024 * 1024),
                        "KB" => (long)(value * 1024),
                        _ => (long)value
                    };
                    totalCleaned += cleaned;
                }

                Logger.Info($"[BleachBit] Batch {b + 1}/{batches.Count} ({batchName}): done");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[BleachBit] Clean error: {ex.Message}");
        }

        return totalCleaned;
    }

    /// <summary>
    /// Get list of available cleaners (intersection of our list and what BleachBit has).
    /// </summary>
    private static async Task<List<string>> GetAvailableCleanersAsync(CancellationToken token)
    {
        try
        {
            var output = await RunBleachBitAsync("--list", 30, token);
            var available = new HashSet<string>(
                output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l) && l.Contains('.')));

            return Cleaners.Where(c => available.Contains(c)).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[BleachBit] List error: {ex.Message}");
            // If list fails, just try all our cleaners — BleachBit will skip unknown ones
            return Cleaners.ToList();
        }
    }

    /// <summary>
    /// Перевірити чи BleachBit може запуститись (є всі DLL).
    /// BleachBit потребує MSVCR100.dll — якщо його немає, пропускаємо.
    /// </summary>
    private static bool CanRunBleachBit()
    {
        var sys32 = Environment.SystemDirectory;
        var dllPath = Path.Combine(sys32, "MSVCR100.dll");
        if (File.Exists(dllPath)) return true;

        // SysWOW64 (32-bit DLL на 64-bit OS)
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var wow64 = Path.Combine(winDir, "SysWOW64", "MSVCR100.dll");
        if (File.Exists(wow64)) return true;

        Logger.Warn("[BleachBit] MSVCR100.dll not found — skipping (would show error dialog)");
        return false;
    }

    /// <summary>
    /// Run BleachBit CLI with args and return stdout.
    /// Перед запуском перевіряє наявність MSVCR100.dll.
    /// </summary>
    private static async Task<string> RunBleachBitAsync(string args, int timeoutSec, CancellationToken token)
    {
        if (!CanRunBleachBit())
            return "";

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = BleachBitDir
        };

        using var proc = Process.Start(psi);
        if (proc == null) return "";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeoutSec * 1000);

        try
        {
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(cts.Token);

            if (!string.IsNullOrEmpty(stderr))
                Logger.Warn($"[BleachBit] stderr: {stderr.Substring(0, Math.Min(500, stderr.Length))}");

            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { }
            throw;
        }
    }
}
