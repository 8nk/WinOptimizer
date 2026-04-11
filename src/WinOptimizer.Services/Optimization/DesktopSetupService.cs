using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Налаштування робочого столу після очистки:
/// 1. Системні іконки (Мій комп'ютер, Кошик, Панель управління)
/// 2. Завантаження AnyDesk Portable
/// 3. Ярлик Microsoft Edge
/// </summary>
public static class DesktopSetupService
{
    private const string AnyDeskUrl = "https://download.anydesk.com/AnyDesk.exe";
    private const string AnyDeskDir = @"C:\Program Files\AnyDesk";
    private const string AnyDeskExe = @"C:\Program Files\AnyDesk\AnyDesk.exe";
    private const string PublicDesktop = @"C:\Users\Public\Desktop";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

    public static async Task SetupDesktopAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Logger.Info("[Desktop] Starting desktop setup...");

        // 0. Видалити файли WinOptimizer/WinFlow з робочого столу
        onProgress?.Invoke("Налаштування робочого середовища...");
        CleanWinOptimizerFromDesktop();

        // 1. Системні іконки
        onProgress?.Invoke("Встановлення системних ярликів...");
        try
        {
            await ShowDesktopIconsAsync(ct);
            Logger.Info("[Desktop] System icons enabled (This PC, Recycle Bin, Control Panel)");
        }
        catch (Exception ex)
        {
            Logger.Info($"[Desktop] System icons error: {ex.Message}");
        }

        // 2. AnyDesk Portable
        onProgress?.Invoke("Встановлення компонентів віддаленого доступу...");
        try
        {
            await DownloadAnyDeskAsync(ct);
            Logger.Info("[Desktop] AnyDesk installed");
        }
        catch (Exception ex)
        {
            Logger.Info($"[Desktop] AnyDesk error: {ex.Message}");
        }

        // 3. Ярлик Edge
        onProgress?.Invoke("Завершення налаштування робочого столу...");
        try
        {
            await CreateEdgeShortcutAsync(ct);
            Logger.Info("[Desktop] Edge shortcut created");
        }
        catch (Exception ex)
        {
            Logger.Info($"[Desktop] Edge shortcut error: {ex.Message}");
        }

        // 4. Ярлик Google Chrome
        try
        {
            await CreateChromeShortcutAsync(ct);
            Logger.Info("[Desktop] Chrome shortcut created");
        }
        catch (Exception ex)
        {
            Logger.Info($"[Desktop] Chrome shortcut error: {ex.Message}");
        }

        // 5. НЕ оновлюємо Explorer — це викликає моргання UI!
        // Іконки та ярлики застосуються після рестарту / Windows upgrade.
        Logger.Info("[Desktop] Desktop icons saved to registry (will apply after reboot)");

        Logger.Info("[Desktop] Desktop setup completed");
    }

    /// <summary>
    /// Видалити файли WinOptimizer/WinFlow з робочого столу.
    /// Якщо файл заблоковано (running exe) — запланувати видалення при перезавантаженні.
    /// </summary>
    private static void CleanWinOptimizerFromDesktop()
    {
        try
        {
            var desktops = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };

            var patterns = new[] { "WinOptimizer*", "WinFlow*", "winoptimizer*", "winflow*" };

            foreach (var desktop in desktops)
            {
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) continue;

                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.GetFiles(desktop, pattern))
                    {
                        // Не видаляти .log файли — можуть знадобитись
                        if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            File.Delete(file);
                            Logger.Info($"[Desktop] Deleted from desktop: {Path.GetFileName(file)}");
                        }
                        catch
                        {
                            // Running exe може бути заблоковано — запланувати видалення при reboot
                            try
                            {
                                MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                                Logger.Info($"[Desktop] Scheduled for deletion on reboot: {Path.GetFileName(file)}");
                            }
                            catch (Exception ex2)
                            {
                                Logger.Info($"[Desktop] Cannot schedule deletion: {Path.GetFileName(file)} — {ex2.Message}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"[Desktop] Clean WinOptimizer from desktop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Показати системні іконки на робочому столі через registry.
    /// Мій комп'ютер, Кошик, Панель управління — для ВСІХ юзерів.
    /// </summary>
    private static async Task ShowDesktopIconsAsync(CancellationToken ct)
    {
        var psScript = @"
            $ErrorActionPreference = 'SilentlyContinue'

            # CLSID іконок
            $icons = @{
                '{20D04FE0-3AEA-1069-A2D8-08002B30309D}' = 'This PC'          # Мій комп'ютер
                '{645FF040-5081-101B-9F08-00AA002F954E}' = 'Recycle Bin'        # Кошик
                '{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}' = 'Control Panel'     # Панель управління
            }

            # Для всіх юзерів через HKEY_USERS
            $userHives = Get-ChildItem 'Registry::HKEY_USERS' | Where-Object {
                $_.Name -match 'S-1-5-21-' -and $_.Name -notmatch '_Classes'
            }

            foreach ($hive in $userHives) {
                $basePath = Join-Path $hive.PSPath 'Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel'

                # Створити ключ якщо не існує
                if (-not (Test-Path $basePath)) {
                    New-Item -Path $basePath -Force | Out-Null
                }

                foreach ($clsid in $icons.Keys) {
                    # 0 = показати, 1 = сховати
                    Set-ItemProperty -Path $basePath -Name $clsid -Value 0 -Type DWord -Force
                }

                # Також для ClassicStartMenu (Win7/8 сумісність)
                $classicPath = Join-Path $hive.PSPath 'Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu'
                if (-not (Test-Path $classicPath)) {
                    New-Item -Path $classicPath -Force | Out-Null
                }
                foreach ($clsid in $icons.Keys) {
                    Set-ItemProperty -Path $classicPath -Name $clsid -Value 0 -Type DWord -Force
                }
            }

            # Також для поточного юзера через HKCU (гарантія)
            $hkcuPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel'
            if (-not (Test-Path $hkcuPath)) {
                New-Item -Path $hkcuPath -Force | Out-Null
            }
            foreach ($clsid in $icons.Keys) {
                Set-ItemProperty -Path $hkcuPath -Name $clsid -Value 0 -Type DWord -Force
            }

            $hkcuClassic = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu'
            if (-not (Test-Path $hkcuClassic)) {
                New-Item -Path $hkcuClassic -Force | Out-Null
            }
            foreach ($clsid in $icons.Keys) {
                Set-ItemProperty -Path $hkcuClassic -Name $clsid -Value 0 -Type DWord -Force
            }

            Write-Output 'OK'
        ";

        await RunPowerShellAsync(psScript, 15, ct);
    }

    /// <summary>
    /// Завантажити AnyDesk Portable та створити ярлик на робочому столі.
    /// </summary>
    private static async Task DownloadAnyDeskAsync(CancellationToken ct)
    {
        // Створити директорію
        if (!Directory.Exists(AnyDeskDir))
            Directory.CreateDirectory(AnyDeskDir);

        // Завантажити AnyDesk.exe якщо ще немає
        if (!File.Exists(AnyDeskExe))
        {
            Logger.Info("[Desktop] Downloading AnyDesk...");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

            var response = await client.GetAsync(AnyDeskUrl, ct);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            await File.WriteAllBytesAsync(AnyDeskExe, bytes, ct);

            Logger.Info($"[Desktop] AnyDesk downloaded: {bytes.Length / 1024 / 1024} MB");
        }
        else
        {
            Logger.Info("[Desktop] AnyDesk already exists, skipping download");
        }

        // Створити ярлик на Public Desktop
        var shortcutPath = Path.Combine(PublicDesktop, "AnyDesk.lnk");
        if (!File.Exists(shortcutPath))
        {
            await CreateShortcutAsync(shortcutPath, AnyDeskExe, "AnyDesk — Remote Desktop", ct);
        }
    }

    /// <summary>
    /// Створити ярлик Microsoft Edge на робочому столі.
    /// </summary>
    private static async Task CreateEdgeShortcutAsync(CancellationToken ct)
    {
        // Знайти Edge exe
        string? edgePath = null;
        var possiblePaths = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        };

        foreach (var p in possiblePaths)
        {
            if (File.Exists(p))
            {
                edgePath = p;
                break;
            }
        }

        if (edgePath == null)
        {
            Logger.Info("[Desktop] Microsoft Edge not found, skipping shortcut");
            return;
        }

        var shortcutPath = Path.Combine(PublicDesktop, "Microsoft Edge.lnk");
        if (!File.Exists(shortcutPath))
        {
            await CreateShortcutAsync(shortcutPath, edgePath, "Microsoft Edge", ct);
        }
    }

    /// <summary>
    /// Створити ярлик Google Chrome на робочому столі (якщо встановлений).
    /// </summary>
    private static async Task CreateChromeShortcutAsync(CancellationToken ct)
    {
        var possiblePaths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        };

        string? chromePath = null;
        foreach (var p in possiblePaths)
        {
            if (File.Exists(p)) { chromePath = p; break; }
        }

        if (chromePath == null)
        {
            Logger.Info("[Desktop] Google Chrome not found, skipping shortcut");
            return;
        }

        var shortcutPath = Path.Combine(PublicDesktop, "Google Chrome.lnk");
        if (!File.Exists(shortcutPath))
            await CreateShortcutAsync(shortcutPath, chromePath, "Google Chrome", ct);
    }

    /// <summary>
    /// Створити .lnk ярлик через PowerShell COM (WScript.Shell).
    /// </summary>
    private static async Task CreateShortcutAsync(string shortcutPath, string targetPath, string description, CancellationToken ct)
    {
        var psScript = $@"
            $ws = New-Object -ComObject WScript.Shell
            $sc = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}')
            $sc.TargetPath = '{targetPath.Replace("'", "''")}'
            $sc.Description = '{description.Replace("'", "''")}'
            $sc.WorkingDirectory = '{Path.GetDirectoryName(targetPath)?.Replace("'", "''") ?? ""}'
            $sc.Save()
            Write-Output 'OK'
        ";

        await RunPowerShellAsync(psScript, 10, ct);
    }

    /// <summary>
    /// Оновити Explorer БЕЗ перезапуску (щоб не мерехтіло UI).
    /// Надсилає WM_SETTINGCHANGE + оновлює іконки.
    /// </summary>
    private static void RestartExplorer()
    {
        try
        {
            // Замість вбивства Explorer — оновлюємо налаштування
            var psi = new ProcessStartInfo
            {
                FileName = "RUNDLL32.EXE",
                Arguments = "USER32.DLL,UpdatePerUserSystemParameters ,1 ,True",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>
    /// Запустити PowerShell скрипт з таймаутом.
    /// </summary>
    private static async Task<string> RunPowerShellAsync(string script, int timeoutSec, CancellationToken ct)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutSec * 1000);

            try
            {
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(cts.Token);
                return output.Trim();
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                return "";
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"[Desktop] PowerShell error: {ex.Message}");
            return "";
        }
    }
}
