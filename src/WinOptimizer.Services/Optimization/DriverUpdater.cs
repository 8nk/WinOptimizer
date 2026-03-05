using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оновлення драйверів через PnPUtil.
/// CRITICAL: pnputil.exe тільки в System32/Sysnative (не в 32-bit SysWOW64).
/// </summary>
public static class DriverUpdater
{
    public static async Task<bool> UpdateAsync(Action<string>? onProgress = null)
    {
        try
        {
            onProgress?.Invoke("Сканування драйверів...");
            Logger.Info("Starting driver scan");

            var sys32 = GetRealSystem32();

            // PnPUtil scan
            var result = await Task.Run(() =>
            {
                try
                {
                    var pnpPath = Path.Combine(sys32, "pnputil.exe");
                    Logger.Info($"PnPUtil path: {pnpPath} (exists: {File.Exists(pnpPath)})");

                    var psi = new ProcessStartInfo
                    {
                        FileName = pnpPath,
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
                        var usoPath = Path.Combine(sys32, "UsoClient.exe");
                        var psi = new ProcessStartInfo
                        {
                            FileName = usoPath,
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
