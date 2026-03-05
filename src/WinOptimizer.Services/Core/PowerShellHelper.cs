namespace WinOptimizer.Services.Core;

/// <summary>
/// Централізований хелпер для PowerShell.
/// КРИТИЧНО: app компілюється як win-x86 (32-bit), тому на 64-bit Windows
/// потрібно використовувати Sysnative\PowerShell для доступу до 64-bit cmdlets
/// (Restore-Computer, Start-MpScan, Set-Service, DISM, etc.)
/// </summary>
public static class PowerShellHelper
{
    private static string? _cachedPath;

    /// <summary>
    /// Повертає повний шлях до 64-bit PowerShell (Sysnative) або fallback до звичайного.
    /// </summary>
    public static string Path
    {
        get
        {
            if (_cachedPath != null) return _cachedPath;

            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            // On 64-bit OS running as 32-bit process: use Sysnative to get 64-bit PowerShell
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                var sysnative = System.IO.Path.Combine(winDir, "Sysnative",
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(sysnative))
                {
                    _cachedPath = sysnative;
                    return _cachedPath;
                }
            }

            // Normal path (64-bit process or 32-bit OS)
            var system32 = System.IO.Path.Combine(winDir, "System32",
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(system32))
            {
                _cachedPath = system32;
                return _cachedPath;
            }

            // Last resort
            _cachedPath = "powershell.exe";
            return _cachedPath;
        }
    }
}
