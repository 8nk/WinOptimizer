using System.Diagnostics;
using WinOptimizer.Core.Models;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

/// <summary>
/// Аналіз системи перед оптимізацією.
/// Визначає розмір temp/cache/кошика, кількість служб, SSD/HDD.
/// </summary>
public static class SystemAnalyzer
{
    public static async Task<SystemScanResult> ScanAsync(Action<string>? onProgress = null)
    {
        var result = new SystemScanResult();

        try
        {
            // Temp files
            onProgress?.Invoke("Сканування тимчасових файлів...");
            result.TempFilesSize = await Task.Run(() => GetDirectorySize(GetTempPaths()));
            Logger.Info($"Temp files: {SystemScanResult.FormatSize(result.TempFilesSize)}");

            // Browser cache
            onProgress?.Invoke("Сканування кешу браузерів...");
            result.BrowserCacheSize = await Task.Run(() => GetDirectorySize(GetBrowserCachePaths()));
            Logger.Info($"Browser cache: {SystemScanResult.FormatSize(result.BrowserCacheSize)}");

            // Recycle bin
            onProgress?.Invoke("Сканування кошика...");
            result.RecycleBinSize = await Task.Run(() => GetRecycleBinSize());
            Logger.Info($"Recycle bin: {SystemScanResult.FormatSize(result.RecycleBinSize)}");

            // Windows logs
            onProgress?.Invoke("Сканування логів Windows...");
            result.WindowsLogsSize = await Task.Run(() => GetDirectorySize(GetWindowsLogPaths()));
            Logger.Info($"Windows logs: {SystemScanResult.FormatSize(result.WindowsLogsSize)}");

            // Services
            onProgress?.Invoke("Аналіз служб Windows...");
            result.DisableableServicesCount = await Task.Run(() => CountDisableableServices());
            Logger.Info($"Disableable services: {result.DisableableServicesCount}");

            // Startup items
            onProgress?.Invoke("Аналіз автозавантаження...");
            result.StartupItemsCount = await Task.Run(() => CountStartupItems());
            Logger.Info($"Startup items: {result.StartupItemsCount}");

            // SSD detection
            onProgress?.Invoke("Визначення типу диска...");
            result.IsSsd = await Task.Run(() => DetectSsd());
            Logger.Info($"SSD: {result.IsSsd}");

            // Free space
            var cDrive = new DriveInfo("C");
            result.FreeSpaceBefore = cDrive.AvailableFreeSpace;
            result.TotalDiskSize = cDrive.TotalSize;
            Logger.Info($"Free space: {SystemScanResult.FormatSize(result.FreeSpaceBefore)} / {SystemScanResult.FormatSize(result.TotalDiskSize)}");
        }
        catch (Exception ex)
        {
            Logger.Error("SystemScan failed", ex);
        }

        return result;
    }

    private static string[] GetTempPaths()
    {
        return new[]
        {
            Path.GetTempPath(),
            @"C:\Windows\Temp",
            @"C:\Windows\Prefetch"
        };
    }

    private static string[] GetBrowserCachePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return new[]
        {
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Cache"),
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Code Cache"),
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Cache"),
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Code Cache"),
            Path.Combine(appData, @"Mozilla\Firefox\Profiles"),
        };
    }

    private static string[] GetWindowsLogPaths()
    {
        return new[]
        {
            @"C:\Windows\Logs",
            @"C:\Windows\Panther",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
        };
    }

    private static long GetDirectorySize(string[] paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
            }
            catch { }
        }
        return total;
    }

    private static long GetRecycleBinSize()
    {
        try
        {
            // PowerShell: (New-Object -ComObject Shell.Application).NameSpace(0xa).Items() | Measure-Object -Property Size -Sum
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"try { $shell = New-Object -ComObject Shell.Application; $rb = $shell.NameSpace(0xa); if($rb) { ($rb.Items() | Measure-Object -Property Size -Sum).Sum } else { 0 } } catch { 0 }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            return long.TryParse(output, out var size) ? size : 0;
        }
        catch { return 0; }
    }

    private static int CountDisableableServices()
    {
        // Список служб що безпечно вимикати
        var services = new[] { "DiagTrack", "SysMain", "WSearch", "dmwappushservice",
            "MapsBroker", "lfsvc", "RetailDemo", "wisvc" };
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"@({string.Join(",", services.Select(s => $"'{s}'"))}) | ForEach-Object {{ $svc = Get-Service -Name $_ -ErrorAction SilentlyContinue; if($svc -and $svc.StartType -ne 'Disabled') {{ $_.ToString() }} }} | Measure-Object | Select-Object -ExpandProperty Count\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return services.Length;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            return int.TryParse(output, out var c) ? c : services.Length;
        }
        catch { return services.Length; }
    }

    private static int CountStartupItems()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"$count = 0; $paths = @('HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run'); foreach($p in $paths) { if(Test-Path $p) { $count += (Get-ItemProperty $p -ErrorAction SilentlyContinue).PSObject.Properties.Where({$_.Name -notlike 'PS*'}).Count } }; $count\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            return int.TryParse(output, out var c) ? c : 0;
        }
        catch { return 0; }
    }

    private static bool DetectSsd()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"(Get-PhysicalDisk | Where-Object DeviceID -eq 0).MediaType\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return output.Contains("SSD", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
