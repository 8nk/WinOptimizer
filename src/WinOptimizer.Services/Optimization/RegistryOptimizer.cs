using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оптимізація реєстру — візуальні ефекти, швидкість меню тощо.
/// Безпечні зміни що покращують швидкодію.
/// </summary>
public static class RegistryOptimizer
{
    public static async Task OptimizeAsync(Action<string>? onProgress = null)
    {
        try
        {
            onProgress?.Invoke("Оптимізація реєстру...");

            await Task.Run(() =>
            {
                // Зменшити затримку меню
                SetRegistryValue(@"HKCU:\Control Panel\Desktop", "MenuShowDelay", "50");

                // Вимкнути анімації
                SetRegistryValue(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0);

                // Швидше завершення процесів
                SetRegistryValue(@"HKCU:\Control Panel\Desktop", "WaitToKillAppTimeout", "2000");
                SetRegistryValue(@"HKCU:\Control Panel\Desktop", "HungAppTimeout", "2000");

                // Вимкнути Windows Tips
                SetRegistryValue(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0);

                Logger.Info("Registry optimization completed");
            });
        }
        catch (Exception ex)
        {
            Logger.Error("RegistryOptimizer failed", ex);
        }
    }

    private static void SetRegistryValue(string path, string name, object value)
    {
        try
        {
            var valueStr = value is int i ? i.ToString() : $"'{value}'";
            var typeFlag = value is int ? "-Type DWord" : "";

            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"if(!(Test-Path '{path}')) {{ New-Item -Path '{path}' -Force | Out-Null }}; Set-ItemProperty -Path '{path}' -Name '{name}' -Value {valueStr} {typeFlag} -Force\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Logger.Error($"SetRegistryValue {path}\\{name}", ex);
        }
    }
}
