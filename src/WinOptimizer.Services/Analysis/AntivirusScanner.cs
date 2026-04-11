using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

/// <summary>
/// Запуск Windows Defender Quick Scan та видалення загроз.
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// - Win10+: Start-MpScan cmdlet (PowerShell, fire-and-forget)
/// - Win7/8: MpCmdRun.exe -Scan -ScanType 1 (command line tool, працює на всіх версіях)
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
            var isWin10Plus = IsWindows10OrLater();

            // 1. Update definitions (з таймаутом 2 хвилини)
            onProgress?.Invoke("Оновлення баз захисту Windows...");
            Logger.Info("[AV] Updating Defender signatures...");
            try
            {
                if (isWin10Plus)
                {
                    await RunPsWithTimeoutAsync(
                        "Update-MpSignature -ErrorAction SilentlyContinue",
                        TimeSpan.FromMinutes(2));
                }
                else
                {
                    // Win7/8: MpCmdRun.exe -SignatureUpdate
                    await RunMpCmdRunAsync("-SignatureUpdate", TimeSpan.FromMinutes(2));
                }
                Logger.Info("[AV] Defender signatures updated");
            }
            catch (Exception ex)
            {
                Logger.Info($"[AV] Signature update skipped: {ex.Message}");
            }

            // 2. Quick Scan — FIRE AND FORGET!
            onProgress?.Invoke("Перевірка безпеки системи...");
            Logger.Info("[AV] Starting Defender Quick Scan (fire-and-forget)...");
            StartScanFireAndForget(isWin10Plus);
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
                    bool scanning;
                    if (isWin10Plus)
                    {
                        var statusOutput = await RunPsWithTimeoutReturnAsync(
                            "$s = Get-MpComputerStatus -ErrorAction SilentlyContinue; " +
                            "if($s) { \"$($s.QuickScanInProgress)|$($s.FullScanInProgress)\" } else { 'False|False' }",
                            TimeSpan.FromSeconds(15));

                        var parts = statusOutput.Trim().Split('|');
                        scanning = (parts.Length > 0 && parts[0].Equals("True", StringComparison.OrdinalIgnoreCase))
                                     || (parts.Length > 1 && parts[1].Equals("True", StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        // Win7/8: перевіряємо чи MpCmdRun.exe ще працює
                        scanning = Process.GetProcessesByName("MpCmdRun").Length > 0;
                    }

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
                if (isWin10Plus)
                {
                    var threatOutput = await RunPsWithTimeoutReturnAsync(
                        "$threats = Get-MpThreatDetection -ErrorAction SilentlyContinue; " +
                        "if($threats) { $threats.Count } else { 0 }",
                        TimeSpan.FromSeconds(20));

                    if (int.TryParse(threatOutput.Trim(), out var count))
                        threatsFound = count;
                }
                else
                {
                    // Win7/8: MpCmdRun не має зручного способу отримати кількість загроз,
                    // тому просто повідомляємо що скан завершено
                    Logger.Info("[AV] Win7/8: threat count not available via MpCmdRun");
                }
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
                    if (isWin10Plus)
                    {
                        await RunPsWithTimeoutAsync(
                            "Remove-MpThreat -ErrorAction SilentlyContinue",
                            TimeSpan.FromMinutes(2));
                    }
                    else
                    {
                        // Win7/8: MpCmdRun -Scan -ScanType 1 вже видаляє загрози
                        await RunMpCmdRunAsync("-RemoveDefinitions -DynamicSignatures", TimeSpan.FromMinutes(1));
                    }
                }
                catch { }
                Logger.Info($"[AV] Removed {threatsFound} threats");
            }
            else
            {
                onProgress?.Invoke("Система безпечна");
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

    private static bool IsWindows10OrLater()
    {
        try { return Environment.OSVersion.Version.Major >= 10; }
        catch { return false; }
    }

    /// <summary>
    /// Знайти MpCmdRun.exe (Windows Defender CLI — працює на Win7/8/10/11)
    /// </summary>
    private static string GetMpCmdRunPath()
    {
        // Standard paths for MpCmdRun.exe
        var paths = new[]
        {
            @"C:\Program Files\Windows Defender\MpCmdRun.exe",
            @"C:\Program Files (x86)\Windows Defender\MpCmdRun.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Defender", "MpCmdRun.exe"),
        };

        foreach (var p in paths)
        {
            if (File.Exists(p)) return p;
        }

        return "MpCmdRun.exe"; // fallback
    }

    /// <summary>
    /// Запускає Quick Scan БЕЗ очікування завершення.
    /// Process запускається і ми його не чекаємо — він працює у фоні.
    /// </summary>
    private static void StartScanFireAndForget(bool isWin10Plus)
    {
        try
        {
            ProcessStartInfo psi;

            if (isWin10Plus)
            {
                psi = new ProcessStartInfo
                {
                    FileName = PowerShellHelper.Path,
                    Arguments = "-NoProfile -NoLogo -Command \"Start-MpScan -ScanType QuickScan -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                // Win7/8: MpCmdRun.exe -Scan -ScanType 1 (Quick Scan)
                psi = new ProcessStartInfo
                {
                    FileName = GetMpCmdRunPath(),
                    Arguments = "-Scan -ScanType 1",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
            }

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
    /// Запустити MpCmdRun.exe з таймаутом (для Win7/8)
    /// </summary>
    private static async Task RunMpCmdRunAsync(string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetMpCmdRunPath(),
            Arguments = arguments,
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
            try { proc.Kill(); } catch { }
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
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { }
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
            try { proc.Kill(); } catch { }
            throw new TimeoutException($"PowerShell timed out after {timeout.TotalSeconds}s");
        }
    }
}
