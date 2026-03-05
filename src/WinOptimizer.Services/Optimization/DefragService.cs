using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Дефрагментація (HDD) або TRIM (SSD).
/// HDD defrag може тривати 10-30 хв на повільних дисках — використовуємо async polling.
/// </summary>
public static class DefragService
{
    public static async Task<bool> OptimizeAsync(bool isSsd, Action<string>? onProgress = null)
    {
        try
        {
            if (isSsd)
            {
                onProgress?.Invoke("Виконання TRIM для SSD...");
                Logger.Info("Running TRIM on SSD");
                return await RunDefragAsync("Optimize-Volume -DriveLetter C -ReTrim -Verbose",
                    TimeSpan.FromMinutes(3), onProgress);
            }
            else
            {
                onProgress?.Invoke("Дефрагментація HDD...");
                Logger.Info("Running Defrag on HDD");
                return await RunDefragAsync("Optimize-Volume -DriveLetter C -Defrag -Verbose",
                    TimeSpan.FromMinutes(15), onProgress);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DefragService failed", ex);
            return false;
        }
    }

    private static async Task<bool> RunDefragAsync(string command, TimeSpan maxWait, Action<string>? onProgress)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var startTime = DateTime.Now;

            // Async wait with timeout — не блокує, не крашиться
            while (!proc.HasExited)
            {
                var elapsed = DateTime.Now - startTime;
                if (elapsed > maxWait)
                {
                    Logger.Warn($"Defrag timeout after {elapsed.TotalMinutes:F0} min, killing...");
                    try { proc.Kill(); } catch { }
                    onProgress?.Invoke("Оптимізація диску: таймаут, пропускаємо...");
                    return false;
                }

                onProgress?.Invoke($"Оптимізація диску... ({elapsed.Minutes}:{elapsed.Seconds:D2})");
                await Task.Delay(3000);
            }

            var exitCode = proc.ExitCode;
            Logger.Info($"Defrag exit code: {exitCode}, time: {(DateTime.Now - startTime).TotalSeconds:F0}s");
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Defrag process error", ex);
            return false;
        }
    }
}
