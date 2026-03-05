using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

/// <summary>
/// Запуск Windows Defender Quick Scan та видалення загроз.
/// CRITICAL FIX: Start-MpScan запускається АСИНХРОННО (fire-and-forget),
/// інакше PowerShell блокує на 20-30 хв на повільних ПК!
/// </summary>
public static class AntivirusScanner
{
    public static async Task<int> RunFullScanAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        int threatsFound = 0;

        try
        {
            // 1. Update definitions (з таймаутом 2 хвилини)
            onProgress?.Invoke("Оновлення антивірусних баз...");
            Logger.Info("[AV] Updating Defender signatures...");
            try
            {
                await RunPsWithTimeoutAsync(
                    "Update-MpSignature -ErrorAction SilentlyContinue",
                    TimeSpan.FromMinutes(2));
                Logger.Info("[AV] Defender signatures updated");
            }
            catch (Exception ex)
            {
                Logger.Info($"[AV] Signature update skipped: {ex.Message}");
            }

            // 2. Quick Scan — FIRE AND FORGET!
            // Start-MpScan в PowerShell БЛОКУЄ до завершення скану.
            // На повільних ПК це 20-30 хвилин → зависає вся програма.
            // Тому запускаємо процес і НЕ чекаємо завершення.
            onProgress?.Invoke("Швидке сканування системи...");
            Logger.Info("[AV] Starting Defender Quick Scan (fire-and-forget)...");
            StartScanFireAndForget();
            Logger.Info("[AV] Quick Scan command sent (async, not blocking)");

            // 3. Невелика пауза щоб скан встиг стартувати
            await Task.Delay(2000, token);

            // 4. Poll scan status (максимум 7 хвилин)
            var maxWait = TimeSpan.FromMinutes(7);
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < maxWait)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(5000, token); // перевіряємо кожні 5 сек

                try
                {
                    var statusOutput = await RunPsWithTimeoutReturnAsync(
                        "$s = Get-MpComputerStatus -ErrorAction SilentlyContinue; " +
                        "if($s) { \"$($s.QuickScanInProgress)|$($s.FullScanInProgress)\" } else { 'False|False' }",
                        TimeSpan.FromSeconds(15));

                    var parts = statusOutput.Trim().Split('|');
                    bool scanning = (parts.Length > 0 && parts[0].Equals("True", StringComparison.OrdinalIgnoreCase))
                                 || (parts.Length > 1 && parts[1].Equals("True", StringComparison.OrdinalIgnoreCase));

                    if (!scanning)
                    {
                        var elapsed = DateTime.Now - startTime;
                        Logger.Info($"[AV] Quick Scan completed in {elapsed.TotalSeconds:F0}s");
                        break;
                    }

                    var elapsedTime = DateTime.Now - startTime;
                    onProgress?.Invoke($"Сканування... ({elapsedTime.Minutes}:{elapsedTime.Seconds:D2})");
                }
                catch (TimeoutException)
                {
                    Logger.Info("[AV] Status check timed out, continuing...");
                }
                catch (Exception ex)
                {
                    Logger.Info($"[AV] Status check error: {ex.Message}");
                }
            }

            var totalElapsed = DateTime.Now - startTime;
            if (totalElapsed >= maxWait)
                Logger.Info($"[AV] Scan timeout after {totalElapsed.TotalMinutes:F0}min, continuing...");

            // 5. Check for threats (з таймаутом)
            try
            {
                var threatOutput = await RunPsWithTimeoutReturnAsync(
                    "$threats = Get-MpThreatDetection -ErrorAction SilentlyContinue; " +
                    "if($threats) { $threats.Count } else { 0 }",
                    TimeSpan.FromSeconds(20));

                if (int.TryParse(threatOutput.Trim(), out var count))
                    threatsFound = count;
            }
            catch (Exception ex)
            {
                Logger.Info($"[AV] Threat check error: {ex.Message}");
            }

            // 6. Remove threats if found
            if (threatsFound > 0)
            {
                onProgress?.Invoke($"Знайдено загроз: {threatsFound}. Видалення...");
                try
                {
                    await RunPsWithTimeoutAsync(
                        "Remove-MpThreat -ErrorAction SilentlyContinue",
                        TimeSpan.FromMinutes(2));
                }
                catch { }
                Logger.Info($"[AV] Removed {threatsFound} threats");
            }
            else
            {
                onProgress?.Invoke("Загроз не знайдено");
                Logger.Info("[AV] No threats found");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[AV] AntivirusScanner error: {ex.Message}");
        }

        return threatsFound;
    }

    /// <summary>
    /// Запускає Quick Scan БЕЗ очікування завершення.
    /// Process запускається і ми його не чекаємо — він працює у фоні.
    /// </summary>
    private static void StartScanFireAndForget()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = "-NoProfile -NoLogo -Command \"Start-MpScan -ScanType QuickScan -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            Logger.Info($"[AV] Scan process started, PID: {proc?.Id}");
            // НЕ чекаємо завершення! Процес працює у фоні.
        }
        catch (Exception ex)
        {
            Logger.Error($"[AV] Failed to start scan process: {ex.Message}");
        }
    }

    /// <summary>
    /// Виконує PowerShell команду з таймаутом і повертає output.
    /// </summary>
    private static async Task<string> RunPsWithTimeoutReturnAsync(string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellHelper.Path,
            Arguments = $"-NoProfile -NoLogo -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "";

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException($"PowerShell timed out after {timeout.TotalSeconds}s");
        }
    }

    private static async Task RunPsWithTimeoutAsync(string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellHelper.Path,
            Arguments = $"-NoProfile -NoLogo -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return;

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException($"PowerShell timed out after {timeout.TotalSeconds}s");
        }
    }
}
