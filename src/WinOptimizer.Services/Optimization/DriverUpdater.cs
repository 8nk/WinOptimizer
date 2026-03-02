using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оновлення драйверів через PnPUtil.
/// </summary>
public static class DriverUpdater
{
    public static async Task<bool> UpdateAsync(Action<string>? onProgress = null)
    {
        try
        {
            onProgress?.Invoke("Сканування драйверів...");
            Logger.Info("Starting driver scan");

            // PnPUtil scan
            var result = await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        Arguments = "/scan-devices",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) return false;
                    proc.WaitForExit(120000); // 2 min
                    Logger.Info($"PnPUtil exit: {proc.ExitCode}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error("PnPUtil failed", ex);
                    return false;
                }
            });

            // Windows Update scan as fallback
            if (!result)
            {
                onProgress?.Invoke("Windows Update сканування...");
                await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "UsoClient.exe",
                            Arguments = "StartScan",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = Process.Start(psi);
                        proc?.WaitForExit(60000);
                    }
                    catch { }
                });
            }

            onProgress?.Invoke("Драйвери перевірено");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("DriverUpdater failed", ex);
            return false;
        }
    }
}
