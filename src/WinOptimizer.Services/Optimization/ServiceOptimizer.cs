using System.Diagnostics;
using WinOptimizer.Core.Models;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оптимізація служб Windows — вимикає непотрібні служби.
/// Зберігає стан для rollback!
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// - SysMain (Win10+) = Superfetch (Win7/8)
/// - Деякі служби існують тільки на Win10+ (wisvc, MapsBroker, RetailDemo)
/// </summary>
public static class ServiceOptimizer
{
    /// <summary>Отримати список служб для поточної версії Windows</summary>
    private static string[] GetTargetServices()
    {
        var services = new List<string>
        {
            "DiagTrack",        // Connected User Experiences and Telemetry (Win10+, ігнорується на Win7)
            "WSearch",          // Windows Search (всі версії)
            "dmwappushservice", // WAP Push Message Routing (Win8+)
        };

        if (IsWindows10OrLater())
        {
            services.Add("SysMain");     // Superfetch (Win10+ ім'я)
            services.Add("MapsBroker");  // Downloaded Maps Manager (Win10+)
            services.Add("lfsvc");       // Geolocation Service (Win10+)
            services.Add("RetailDemo");  // Retail Demo Service (Win10+)
            services.Add("wisvc");       // Windows Insider Service (Win10+)
        }
        else
        {
            services.Add("SuperFetch");  // Superfetch (Win7/8 ім'я)
        }

        return services.ToArray();
    }

    private static bool IsWindows10OrLater()
    {
        try { return Environment.OSVersion.Version.Major >= 10; }
        catch { return false; }
    }

    public static async Task<List<DisabledService>> OptimizeAsync(Action<string>? onProgress = null)
    {
        var disabled = new List<DisabledService>();

        var targetServices = GetTargetServices();
        foreach (var svcName in targetServices)
        {
            try
            {
                onProgress?.Invoke($"Оптимізація служби: {svcName}...");

                var result = await Task.Run(() => DisableService(svcName));
                if (result != null)
                {
                    disabled.Add(result);
                    Logger.Info($"Disabled service: {svcName} (was: {result.OriginalStartType})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to disable {svcName}", ex);
            }
        }

        Logger.Info($"Total services disabled: {disabled.Count}");
        return disabled;
    }

    private static DisabledService? DisableService(string serviceName)
    {
        try
        {
            // Get current start type
            var getPsi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"$svc = Get-Service -Name '{serviceName}' -ErrorAction SilentlyContinue; if($svc -and $svc.StartType -ne 'Disabled') {{ $svc.StartType.ToString() }} else {{ 'SKIP' }}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var getProc = Process.Start(getPsi);
            if (getProc == null) return null;
            var startType = getProc.StandardOutput.ReadToEnd().Trim();
            getProc.WaitForExit(10000);

            if (startType == "SKIP" || string.IsNullOrEmpty(startType))
                return null; // Already disabled or not found

            // Stop and disable
            var setPsi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"Stop-Service -Name '{serviceName}' -Force -ErrorAction SilentlyContinue; Set-Service -Name '{serviceName}' -StartupType Disabled\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var setProc = Process.Start(setPsi);
            setProc?.WaitForExit(15000);

            // Get display name
            var namePsi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"(Get-Service -Name '{serviceName}' -ErrorAction SilentlyContinue).DisplayName\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var nameProc = Process.Start(namePsi);
            var displayName = nameProc?.StandardOutput.ReadToEnd().Trim() ?? serviceName;
            nameProc?.WaitForExit(5000);

            return new DisabledService
            {
                ServiceName = serviceName,
                DisplayName = displayName,
                OriginalStartType = startType
            };
        }
        catch { return null; }
    }

    /// <summary>Відновити службу (для rollback)</summary>
    public static void RestoreService(DisabledService svc)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"Set-Service -Name '{svc.ServiceName}' -StartupType {svc.OriginalStartType}; Start-Service -Name '{svc.ServiceName}' -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            Logger.Info($"Restored service: {svc.ServiceName} → {svc.OriginalStartType}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to restore {svc.ServiceName}", ex);
        }
    }
}
