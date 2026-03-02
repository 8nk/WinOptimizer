using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Дефрагментація (HDD) або TRIM (SSD).
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
                return await RunPowerShellAsync("Optimize-Volume -DriveLetter C -ReTrim -Verbose");
            }
            else
            {
                onProgress?.Invoke("Дефрагментація HDD...");
                Logger.Info("Running Defrag on HDD");
                return await RunPowerShellAsync("Optimize-Volume -DriveLetter C -Defrag -Verbose");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DefragService failed", ex);
            return false;
        }
    }

    private static async Task<bool> RunPowerShellAsync(string command)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{command}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(300000); // 5 min max
                Logger.Info($"Defrag exit code: {proc.ExitCode}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.Error("PowerShell defrag failed", ex);
                return false;
            }
        });
    }
}
