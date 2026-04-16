using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Дефрагментація (HDD) або TRIM (SSD).
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// - Win7: defrag.exe C: /O (або /U для SSD TRIM)
/// - Win8+: Optimize-Volume cmdlet (PowerShell)
/// </summary>
public static class DefragService
{
    public static async Task<bool> OptimizeAsync(bool isSsd, Action<string>? onProgress = null)
    {
        try
        {
            if (IsWindows8OrLater())
            {
                // Win8+ — використовуємо Optimize-Volume
                if (isSsd)
                {
                    onProgress?.Invoke("Оптимізація SSD накопичувача...");
                    Logger.Info("Running TRIM on SSD (Optimize-Volume)");
                    return await RunDefragAsync("Optimize-Volume -DriveLetter C -ReTrim -Verbose",
                        TimeSpan.FromMinutes(3), onProgress, usePowerShell: true);
                }
                else
                {
                    onProgress?.Invoke("Оптимізація жорсткого диска...");
                    Logger.Info("Running Defrag on HDD (Optimize-Volume)");
                    return await RunDefragAsync("Optimize-Volume -DriveLetter C -Defrag -Verbose",
                        TimeSpan.FromMinutes(15), onProgress, usePowerShell: true);
                }
            }
            else
            {
                // Win7 — використовуємо defrag.exe
                if (isSsd)
                {
                    onProgress?.Invoke("Оптимізація SSD накопичувача...");
                    Logger.Info("Running TRIM on SSD (defrag.exe)");
                    // Win7 не підтримує TRIM через defrag, просто пропускаємо
                    Logger.Info("Win7 does not support TRIM via defrag.exe, skipping...");
                    onProgress?.Invoke("Пропуск оптимізації SSD (Windows 7)...");
                    return true;
                }
                else
                {
                    onProgress?.Invoke("Оптимізація жорсткого диска...");
                    Logger.Info("Running Defrag on HDD (defrag.exe)");
                    return await RunDefragAsync("C: /O /U", TimeSpan.FromMinutes(15), onProgress,
                        usePowerShell: false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DefragService failed", ex);
            return false;
        }
    }

    private static bool IsWindows8OrLater()
    {
        var ver = Environment.OSVersion.Version;
        // Win8 = 6.2, Win8.1 = 6.3, Win10/11 = 10.0
        return ver.Major > 6 || (ver.Major == 6 && ver.Minor >= 2);
    }

    private static async Task<bool> RunDefragAsync(string command, TimeSpan maxWait,
        Action<string>? onProgress, bool usePowerShell)
    {
        try
        {
            ProcessStartInfo psi;

            if (usePowerShell)
            {
                psi = new ProcessStartInfo
                {
                    FileName = PowerShellHelper.Path,
                    Arguments = $"-NoProfile -Command \"{command}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
            }
            else
            {
                // defrag.exe — пряма команда
                var sys32 = GetRealSystem32();
                psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(sys32, "defrag.exe"),
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Fallback якщо defrag.exe не знайдений
                if (!File.Exists(psi.FileName))
                {
                    psi.FileName = "defrag.exe";
                }
            }

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
                    onProgress?.Invoke("Оптимізація накопичувача завершена...");
                    return false;
                }

                onProgress?.Invoke($"Оптимізація файлової системи... ({elapsed.Minutes}:{elapsed.Seconds:D2})");
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

    private static string GetRealSystem32()
    {
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            var sysnative = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
            if (Directory.Exists(sysnative)) return sysnative;
        }
        return Environment.SystemDirectory;
    }
}
