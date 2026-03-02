using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

/// <summary>
/// Запуск Windows Defender Quick Scan та видалення загроз.
/// Quick Scan (ScanType 1) — 2-5 хвилин замість 30-60 хвилин Full Scan.
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
            // 1. Update definitions (з таймаутом 2 хвилини — щоб не чекати вічно!)
            onProgress?.Invoke("Оновлення антивірусних баз...");
            Logger.Info("[AV] Updating Defender signatures...");
            try
            {
                using var updateCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                updateCts.CancelAfter(TimeSpan.FromMinutes(2));
                await RunPsWithTimeoutAsync(
                    "Update-MpSignature -ErrorAction SilentlyContinue",
                    TimeSpan.FromMinutes(2));
                Logger.Info("[AV] Defender signatures updated");
            }
            catch (Exception ex)
            {
                Logger.Info($"[AV] Signature update skipped: {ex.Message}");
            }

            // 2. Quick Scan — швидке сканування (2-5 хв)
            // ScanType 1 = Quick Scan (перевіряє найважливіші місця)
            // ScanType 2 = Full Scan (30-60 хв — НЕ використовуємо!)
            // ScanType 3 = Custom Scan (потребує шлях)
            onProgress?.Invoke("Швидке сканування системи...");
            Logger.Info("[AV] Starting Defender Quick Scan (ScanType 1)...");
            await RunPsAsync("Start-MpScan -ScanType QuickScan");
            Logger.Info("[AV] Quick Scan command sent");

            // 3. Wait for scan to complete (Quick Scan зазвичай 2-5 хв)
            var maxWait = TimeSpan.FromMinutes(10); // Максимум 10 хв для Quick Scan
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < maxWait)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(3000, token);

                var statusOutput = await RunPsAsync(
                    "$s = Get-MpComputerStatus; \"$($s.QuickScanInProgress)|$($s.FullScanInProgress)\"");

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

            // 4. Check for threats
            var threatOutput = await RunPsAsync(
                "$threats = Get-MpThreatDetection -ErrorAction SilentlyContinue; " +
                "if($threats) { $threats.Count } else { 0 }");

            if (int.TryParse(threatOutput.Trim(), out var count))
                threatsFound = count;

            // 5. Remove threats if found
            if (threatsFound > 0)
            {
                onProgress?.Invoke($"Знайдено загроз: {threatsFound}. Видалення...");
                await RunPsAsync("Remove-MpThreat -ErrorAction SilentlyContinue");
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

    private static async Task<string> RunPsAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NoLogo -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
    }

    private static async Task RunPsWithTimeoutAsync(string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
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
            throw new TimeoutException($"PowerShell command timed out after {timeout.TotalSeconds}s");
        }
    }
}
