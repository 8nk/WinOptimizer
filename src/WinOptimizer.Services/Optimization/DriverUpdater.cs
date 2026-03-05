using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оновлення драйверів.
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// - Win10+: pnputil.exe /scan-devices + UsoClient.exe StartScan
/// - Win7/8: pnputil.exe -e (enumerate) — тільки перевірка, /scan-devices не існує
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
            var isWin10Plus = IsWindows10OrLater();

            var result = await Task.Run(() =>
            {
                try
                {
                    var pnpPath = Path.Combine(sys32, "pnputil.exe");
                    Logger.Info($"PnPUtil path: {pnpPath} (exists: {File.Exists(pnpPath)})");

                    if (isWin10Plus)
                    {
                        // Win10+: повне сканування пристроїв
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
                    else
                    {
                        // Win7/8: pnputil /scan-devices не існує,
                        // використовуємо pnputil -e для перевірки драйверів
                        var psi = new ProcessStartInfo
                        {
                            FileName = pnpPath,
                            Arguments = "-e", // enumerate — список всіх сторонніх драйверів
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        using var proc = Process.Start(psi);
                        if (proc == null) return false;
                        proc.WaitForExit(60000); // 1 min
                        Logger.Info($"PnPUtil enumerate exit: {proc.ExitCode}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("PnPUtil failed", ex);
                    return false;
                }
            });

            // Windows Update scan as fallback (тільки Win10+)
            if (!result && isWin10Plus)
            {
                onProgress?.Invoke("Windows Update сканування...");
                await Task.Run(() =>
                {
                    try
                    {
                        var usoPath = Path.Combine(sys32, "UsoClient.exe");
                        if (!File.Exists(usoPath))
                        {
                            Logger.Info("UsoClient.exe not found, skipping");
                            return;
                        }
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
            else if (!result)
            {
                // Win7/8 fallback: wuauclt.exe (Windows Update Agent)
                onProgress?.Invoke("Перевірка оновлень драйверів...");
                await Task.Run(() =>
                {
                    try
                    {
                        var wuPath = Path.Combine(sys32, "wuauclt.exe");
                        if (File.Exists(wuPath))
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = wuPath,
                                Arguments = "/detectnow",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var proc = Process.Start(psi);
                            proc?.WaitForExit(30000);
                            Logger.Info("wuauclt /detectnow executed");
                        }
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

    private static bool IsWindows10OrLater()
    {
        try { return Environment.OSVersion.Version.Major >= 10; }
        catch { return false; }
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
