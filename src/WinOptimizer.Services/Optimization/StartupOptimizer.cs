using System.Diagnostics;
using WinOptimizer.Core.Models;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оптимізація автозавантаження — видаляє непотрібні записи з реєстру Run.
/// Зберігає стан для rollback!
/// </summary>
public static class StartupOptimizer
{
    /// <summary>Критичні програми які НЕ вимикаємо</summary>
    private static readonly string[] ProtectedNames =
    {
        "SecurityHealth",    // Windows Defender
        "Windows Defender",
        "RealTimeProtection",
        "WinDefend",
        "MsMpEng",
        "SecurityCenter",
    };

    public static async Task<List<DisabledStartupItem>> OptimizeAsync(Action<string>? onProgress = null)
    {
        var disabled = new List<DisabledStartupItem>();

        var regPaths = new[]
        {
            @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
            @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Run",
        };

        foreach (var regPath in regPaths)
        {
            onProgress?.Invoke($"Аналіз {(regPath.StartsWith("HKCU") ? "користувацького" : "системного")} автозавантаження...");

            var items = await Task.Run(() => GetStartupItems(regPath));

            foreach (var item in items)
            {
                // Skip protected items
                if (IsProtected(item.ValueName, item.ValueData))
                {
                    Logger.Info($"Skipping protected startup: {item.ValueName}");
                    continue;
                }

                onProgress?.Invoke($"Вимкнення: {item.ValueName}...");

                var success = await Task.Run(() => DisableStartupItem(item));
                if (success)
                {
                    disabled.Add(item);
                    Logger.Info($"Disabled startup: {item.ValueName} from {regPath}");
                }
            }
        }

        Logger.Info($"Total startup items disabled: {disabled.Count}");
        return disabled;
    }

    private static bool IsProtected(string name, string value)
    {
        var combined = $"{name} {value}".ToLowerInvariant();
        return ProtectedNames.Any(p => combined.Contains(p.ToLowerInvariant()));
    }

    private static List<DisabledStartupItem> GetStartupItems(string regPath)
    {
        var items = new List<DisabledStartupItem>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"if(Test-Path '{regPath}') {{ Get-ItemProperty '{regPath}' | ForEach-Object {{ $_.PSObject.Properties | Where-Object {{ $_.Name -notlike 'PS*' }} | ForEach-Object {{ '{regPath}|' + $_.Name + '|' + $_.Value + '|' + $_.TypeNameOfValue }} }} }}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return items;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length >= 3)
                {
                    items.Add(new DisabledStartupItem
                    {
                        RegistryPath = parts[0],
                        ValueName = parts[1],
                        ValueData = parts[2],
                        ValueKind = parts.Length > 3 ? parts[3] : "String"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"GetStartupItems {regPath}", ex);
        }
        return items;
    }

    private static bool DisableStartupItem(DisabledStartupItem item)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Remove-ItemProperty -Path '{item.RegistryPath}' -Name '{item.ValueName}' -Force -ErrorAction Stop\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(10000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Відновити startup item (для rollback)</summary>
    public static void RestoreStartupItem(DisabledStartupItem item)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Set-ItemProperty -Path '{item.RegistryPath}' -Name '{item.ValueName}' -Value '{item.ValueData}' -Force\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            Logger.Info($"Restored startup: {item.ValueName}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to restore startup {item.ValueName}", ex);
        }
    }
}
