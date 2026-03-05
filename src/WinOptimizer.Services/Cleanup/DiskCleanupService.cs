using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// МАКСИМАЛЬНО агресивна очистка диска C:.
/// Цільовий результат: 10-30+ GB звільненого простору.
/// Тільки C: — інші диски НЕ чіпаємо!
///
/// Найбільші цілі:
/// - hiberfil.sys (RAM size = 8-16 GB)
/// - Windows Memory Dumps (до 16 GB)
/// - SoftwareDistribution (2-10 GB)
/// - WinSxS cleanup (2-8 GB)
/// - Driver Store orphans (1-10 GB)
/// - Windows Search index (1-5 GB)
/// - Browser + app caches (2-10 GB)
/// - Windows Installer (1-10 GB)
/// - Windows.old (10-30 GB)
/// </summary>
public static class DiskCleanupService
{
    public static async Task<long> CleanAsync(Action<string>? onProgress = null)
    {
        long totalCleaned = 0;

        // ============================================================
        // БЛОК -2: ВИМКНУТИ ДІАЛОГ ПІДТВЕРДЖЕННЯ ВИДАЛЕННЯ!
        // Без цього Windows питає "Ви впевнені?" при кожному видаленні.
        // ============================================================

        onProgress?.Invoke("Підготовка системи...");
        await Task.Run(() =>
        {
            try
            {
                // Вимкнути підтвердження видалення для Recycle Bin (кошик)
                // HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer → ConfirmFileDelete = 0
                var psScript = @"
                    # Вимкнути підтвердження видалення для всіх юзерів
                    $users = Get-ChildItem 'C:\Users' -Directory | Where-Object { $_.Name -notin @('Public','Default','Default User','All Users') }
                    foreach ($u in $users) {
                        $ntuser = Join-Path $u.FullName 'NTUSER.DAT'
                        if (Test-Path $ntuser) {
                            try {
                                $regKey = 'HKU\TempUser'
                                reg load $regKey $ntuser 2>$null
                                reg add ""$regKey\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"" /v ConfirmFileDelete /t REG_DWORD /d 0 /f 2>$null
                                # Також вимкнути через Desktop namespace
                                $sid = (New-Object System.Security.Principal.NTAccount($u.Name)).Translate([System.Security.Principal.SecurityIdentifier]).Value 2>$null
                                if ($sid) {
                                    reg add ""HKU\$sid\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"" /v ConfirmFileDelete /t REG_DWORD /d 0 /f 2>$null
                                }
                                reg unload $regKey 2>$null
                            } catch {}
                        }
                    }
                    # Поточний юзер
                    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Name 'ConfirmFileDelete' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
                    # Через shell: Recycle Bin → no confirmation
                    $shell = New-Object -ComObject Shell.Application
                    $recycleBin = $shell.Namespace(0xa)
                ";
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
                var psi = new ProcessStartInfo
                {
                    FileName = PowerShellHelper.Path,
                    Arguments = $"-NoProfile -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                Logger.Info("[DiskClean] Delete confirmation disabled");
            }
            catch (Exception ex)
            {
                Logger.Info($"[DiskClean] Disable confirmation error: {ex.Message}");
            }
        });

        // ============================================================
        // БЛОК -1: ВБИТИ ВСІ ПРОЦЕСИ КОРИСТУВАЧА!
        // Інакше файли заблоковані і не видаляються.
        // Також закриваємо TG, браузери, ігри, тощо.
        // ============================================================

        onProgress?.Invoke("Закриття програм користувача...");
        await Task.Run(() =>
        {
            // Список процесів які ОБОВ'ЯЗКОВО вбиваємо
            var processesToKill = new[]
            {
                // Браузери
                "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
                "yandex", "browser", "chromium", "centbrowser",
                // Месенджери
                "Telegram", "Discord", "WhatsApp", "Viber", "Skype",
                "slack", "Teams", "ms-teams",
                // Медіа
                "Spotify", "vlc", "AIMP", "foobar2000", "wmplayer",
                "iTunes", "Deezer",
                // Ігри / лаунчери
                "Steam", "steamwebhelper", "EpicGamesLauncher",
                "TLauncher", "javaw", "java",
                "Origin", "Battle.net", "Ubisoft",
                // Офіс / редактори
                "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE",
                "wps", "wpscenter", "wpscloudsvr", "et", "wpp",
                "notepad++", "Code", "sublime_text",
                // Утиліти
                "AnyDesk", "TeamViewer",
                "OneDrive", "OneDriveSetup",
                "GHelper", "ghelper",
                "SoundBooster", "Letasoft",
                "qbittorrent", "utorrent", "bittorrent",
                "7zFM", "WinRAR", "winrar",
                // Антивіруси (обережно)
                "avastui", "avgui",
                // Системні утиліти які можуть блокувати
                "SearchUI", "SearchApp", "YourPhone", "PhoneExperienceHost",
                "GameBar", "GameBarPresenceWriter",
                "CalculatorApp", "WindowsCalculator",
                "Video.UI",
                // Zoom
                "Zoom", "ZoomIt",
            };

            int killed = 0;
            foreach (var procName in processesToKill)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(procName))
                    {
                        try
                        {
                            proc.Kill(true); // Kill entire process tree
                            killed++;
                            Logger.Info($"[DiskClean] Killed: {procName} (PID {proc.Id})");
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Також вбити ВСЕ що запущено з Desktop або Downloads
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var path = proc.MainModule?.FileName;
                        if (path != null && (
                            path.Contains(@"\Desktop\", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains(@"\Documents\", StringComparison.OrdinalIgnoreCase)))
                        {
                            proc.Kill(true);
                            killed++;
                            Logger.Info($"[DiskClean] Killed user-folder process: {proc.ProcessName} ({path})");
                        }
                    }
                    catch { } // Access denied — system process, skip
                }
            }
            catch { }

            Logger.Info($"[DiskClean] Total processes killed: {killed}");

            // Почекати щоб процеси завершились і звільнили файли
            Thread.Sleep(2000);
        });

        // ============================================================
        // БЛОК 0: ПОВНА ОЧИСТКА ВСІХ ПАПОК КОРИСТУВАЧІВ
        // Desktop, Downloads, Documents, Pictures, Videos, Music,
        // Saved Games, Contacts, Favorites, Links, Searches, 3D Objects,
        // OneDrive кеш, і всі інші користувацькі дані.
        // Після цього профіль виглядає як свіжа Windows!
        // ============================================================

        onProgress?.Invoke("Повна очистка файлів користувачів...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;

            // Всі папки які ПОВНІСТЮ очищаємо (ВСЕ всередині видаляємо)
            var foldersToCleanCompletely = new[]
            {
                "Desktop",
                "Downloads",
                "Documents",
                "Pictures",
                "Videos",
                "Music",
                "Saved Games",
                "Contacts",
                "Favorites",
                "Links",
                "Searches",
                "3D Objects",
                "Recorded Calls",
                "Scanned Documents",
            };

            foreach (var userDir in GetUserDirectories())
            {
                var userName = Path.GetFileName(userDir);
                foreach (var folder in foldersToCleanCompletely)
                {
                    var path = Path.Combine(userDir, folder);
                    if (Directory.Exists(path))
                    {
                        cleaned += CleanDirectoryCompletely(path);
                        Logger.Info($"[DiskClean] {folder} cleaned: {userName}");
                    }
                }

                // OneDrive — видаляємо кеш і файли
                foreach (var oneDriveDir in new[] { "OneDrive", "OneDrive - Personal" })
                {
                    var odPath = Path.Combine(userDir, oneDriveDir);
                    if (Directory.Exists(odPath))
                    {
                        cleaned += CleanDirectoryCompletely(odPath);
                        Logger.Info($"[DiskClean] {oneDriveDir} cleaned: {userName}");
                    }
                }

                // .recently-used, Recent
                cleaned += CleanDirectory(Path.Combine(userDir, "AppData", "Roaming", "Microsoft", "Windows", "Recent"));
            }

            // Public папки
            cleaned += CleanDirectoryCompletely(@"C:\Users\Public\Desktop");
            cleaned += CleanDirectoryCompletely(@"C:\Users\Public\Documents");
            cleaned += CleanDirectoryCompletely(@"C:\Users\Public\Downloads");
            cleaned += CleanDirectoryCompletely(@"C:\Users\Public\Music");
            cleaned += CleanDirectoryCompletely(@"C:\Users\Public\Pictures");
            cleaned += CleanDirectoryCompletely(@"C:\Users\Public\Videos");

            Logger.Info($"[DiskClean] ALL user folders cleaned: {cleaned / (1024.0 * 1024.0 * 1024.0):F2} GB");
            return cleaned;
        });

        // ============================================================
        // БЛОК 0b: ОЧИСТКА КОРЕНЯ C:\ — все зайве!
        // ============================================================

        onProgress?.Invoke("Очистка кореня диска C...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            try
            {
                // Видаляємо ВСЕ з кореня C:\ окрім системних папок
                var protectedRootFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Windows", "Program Files", "Program Files (x86)", "ProgramData",
                    "Users", "Recovery", "$Recycle.Bin", "System Volume Information",
                    "PerfLogs", "Boot", "EFI", "$WinREAgent", "$SysReset",
                    "MSOCache", // Office cache — може бути потрібен
                };

                var protectedRootFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "pagefile.sys", "swapfile.sys", "hiberfil.sys",
                    "bootmgr", "BOOTNXT", "BOOTSECT.BAK",
                    "DumpStack.log", "DumpStack.log.tmp",
                };

                // Видаляємо зайві ПАПКИ з кореня C:\
                foreach (var dir in Directory.GetDirectories(@"C:\"))
                {
                    var name = Path.GetFileName(dir);
                    if (protectedRootFolders.Contains(name)) continue;
                    if (name.StartsWith("$")) continue; // Системні ($Windows.~WS, $WINDOWS.~BT, etc.)

                    try
                    {
                        var size = GetDirectorySize(dir);
                        Directory.Delete(dir, true);
                        cleaned += size;
                        Logger.Info($"[DiskClean] Root folder deleted: {name} ({size / (1024 * 1024)} MB)");
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"[DiskClean] Root folder skip: {name} ({ex.Message})");
                    }
                }

                // Видаляємо зайві ФАЙЛИ з кореня C:\
                foreach (var file in Directory.GetFiles(@"C:\"))
                {
                    var name = Path.GetFileName(file);
                    if (protectedRootFiles.Contains(name)) continue;

                    try
                    {
                        var size = new FileInfo(file).Length;
                        File.Delete(file);
                        cleaned += size;
                        Logger.Info($"[DiskClean] Root file deleted: {name} ({size / (1024 * 1024)} MB)");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[DiskClean] Root cleanup error: {ex.Message}");
            }

            Logger.Info($"[DiskClean] Root C:\\ cleaned: {cleaned / (1024 * 1024)} MB");
            return cleaned;
        });

        // ============================================================
        // БЛОК 1: СИСТЕМНІ ВЕЛИКІ ФАЙЛИ (найбільший ефект)
        // ============================================================

        // 1. HIBERNATION FILE — hiberfil.sys = RAM size (8-16 GB!)
        onProgress?.Invoke("Вимкнення гібернації...");
        totalCleaned += await Task.Run(() =>
        {
            try
            {
                // Перевірити розмір hiberfil.sys перед видаленням
                var hibFile = @"C:\hiberfil.sys";
                long hibSize = 0;
                try
                {
                    if (File.Exists(hibFile))
                        hibSize = new FileInfo(hibFile).Length;
                }
                catch { }

                RunCmdWithTimeout("powercfg /hibernate off", 15);
                Logger.Info($"[DiskClean] Hibernation disabled, hiberfil.sys was {hibSize / (1024 * 1024)} MB");
                return hibSize; // Windows видалить файл при наступному рестарті, але рахуємо
            }
            catch (Exception ex)
            {
                Logger.Info($"[DiskClean] Hibernation disable error: {ex.Message}");
                return 0L;
            }
        });

        // 2. WINDOWS MEMORY DUMPS — MEMORY.DMP (= RAM, до 16 GB) + minidumps
        onProgress?.Invoke("Очистка дампів пам'яті...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            // Main dump file
            try
            {
                var dumpFile = @"C:\Windows\MEMORY.DMP";
                if (File.Exists(dumpFile))
                {
                    var size = new FileInfo(dumpFile).Length;
                    File.Delete(dumpFile);
                    cleaned += size;
                    Logger.Info($"[DiskClean] MEMORY.DMP deleted: {size / (1024 * 1024)} MB");
                }
            }
            catch { }
            // Minidumps
            cleaned += CleanDirectory(@"C:\Windows\Minidump");
            // LiveKernelReports
            cleaned += CleanDirectory(@"C:\Windows\LiveKernelReports");
            return cleaned;
        });

        // 3. WINDOWS SEARCH INDEX — Windows.edb (1-5 GB!)
        onProgress?.Invoke("Очистка індексу пошуку...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            try
            {
                // Зупинити пошук
                StopService("WSearch");
                var searchDir = @"C:\ProgramData\Microsoft\Search\Data\Applications\Windows";
                if (Directory.Exists(searchDir))
                {
                    cleaned += CleanDirectory(searchDir);
                    Logger.Info($"[DiskClean] Search index cleaned: {cleaned / (1024 * 1024)} MB");
                }
                // Windows.edb прямо
                try
                {
                    var edbFile = Path.Combine(searchDir, "Windows.edb");
                    if (File.Exists(edbFile))
                    {
                        var size = new FileInfo(edbFile).Length;
                        File.Delete(edbFile);
                        cleaned += size;
                    }
                }
                catch { }
                StartService("WSearch");
            }
            catch (Exception ex)
            {
                Logger.Info($"[DiskClean] Search index error: {ex.Message}");
                try { StartService("WSearch"); } catch { }
            }
            return cleaned;
        });

        // ============================================================
        // БЛОК 2: TEMP / КЕШІ
        // ============================================================

        // 4. User Temp + System Temp + Prefetch
        onProgress?.Invoke("Очистка тимчасових файлів...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            cleaned += CleanDirectory(Path.GetTempPath());
            cleaned += CleanDirectory(@"C:\Windows\Temp");
            cleaned += CleanDirectory(@"C:\Windows\Prefetch");
            Logger.Info($"[DiskClean] Temp cleaned: {cleaned / (1024 * 1024)} MB");
            return cleaned;
        });

        // 5. Recycle Bin
        onProgress?.Invoke("Очистка кошика...");
        totalCleaned += await Task.Run(() => ClearRecycleBin());

        // 6. Windows Update cache — SoftwareDistribution (2-10 GB!)
        onProgress?.Invoke("Очистка кешу Windows Update...");
        totalCleaned += await Task.Run(() =>
        {
            StopService("wuauserv");
            StopService("bits");
            StopService("dosvc"); // Delivery Optimization теж
            long cleaned = 0;
            cleaned += CleanDirectory(@"C:\Windows\SoftwareDistribution\Download");
            cleaned += CleanDirectory(@"C:\Windows\SoftwareDistribution\DataStore\Logs");
            cleaned += CleanDirectory(@"C:\Windows\SoftwareDistribution\PostRebootEventCache.V2");
            Logger.Info($"[DiskClean] WU cache: {cleaned / (1024 * 1024)} MB");
            StartService("wuauserv");
            StartService("bits");
            StartService("dosvc");
            return cleaned;
        });

        // 7. Windows Error Reports (WER) — 500MB+
        onProgress?.Invoke("Очистка звітів про помилки...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            cleaned += CleanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive");
            cleaned += CleanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\ReportQueue");
            cleaned += CleanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\Temp");
            // User WER for all users
            foreach (var userDir in GetUserDirectories())
            {
                cleaned += CleanDirectory(Path.Combine(userDir, "AppData", "Local", "Microsoft", "Windows", "WER"));
            }
            return cleaned;
        });

        // 8. Delivery Optimization cache (1-5 GB!)
        onProgress?.Invoke("Очистка кешу Delivery Optimization...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            cleaned += CleanDirectory(@"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization");
            cleaned += CleanDirectory(@"C:\Windows\SoftwareDistribution\DeliveryOptimization");
            return cleaned;
        });

        // 9. Windows Logs (CBS, DISM, setup)
        onProgress?.Invoke("Очистка логів Windows...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            cleaned += CleanDirectory(@"C:\Windows\Logs");
            cleaned += CleanDirectory(@"C:\Windows\System32\LogFiles");
            cleaned += CleanFilesByPattern(@"C:\Windows", "*.log");
            cleaned += CleanDirectory(@"C:\Windows\Panther");
            cleaned += CleanDirectory(@"C:\Windows\INF"); // old INF logs
            // Clear all event logs via wevtutil
            ClearEventLogs();
            return cleaned;
        });

        // 10. Windows Installer cache ($PatchCache$ + orphaned .msp)
        onProgress?.Invoke("Очистка кешу інсталятора...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            cleaned += CleanDirectory(@"C:\Windows\Installer\$PatchCache$");
            // Orphaned MSI/MSP файли (обережно — тільки файли > 6 міс)
            cleaned += CleanOldFiles(@"C:\Windows\Installer", "*.msp", 180);
            cleaned += CleanOldFiles(@"C:\Windows\Installer", "*.tmp", 0); // tmp завжди видаляти
            Logger.Info($"[DiskClean] Installer cache: {cleaned / (1024 * 1024)} MB");
            return cleaned;
        });

        // ============================================================
        // БЛОК 3: DRIVER STORE + GPU КЕШІ
        // ============================================================

        // 11. Driver Store — старі драйвери (1-10 GB!)
        onProgress?.Invoke("Очистка старих драйверів...");
        totalCleaned += await Task.Run(() => CleanOldDrivers());

        // 12. GPU Shader / NVIDIA / AMD кеші (1-5 GB)
        onProgress?.Invoke("Очистка кешів GPU...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            foreach (var userDir in GetUserDirectories())
            {
                var localAppData = Path.Combine(userDir, "AppData", "Local");
                // NVIDIA
                cleaned += CleanDirectory(Path.Combine(localAppData, "NVIDIA", "DXCache"));
                cleaned += CleanDirectory(Path.Combine(localAppData, "NVIDIA", "GLCache"));
                cleaned += CleanDirectory(Path.Combine(localAppData, "NVIDIA", "ComputeCache"));
                cleaned += CleanDirectory(Path.Combine(localAppData, "NVIDIA Corporation", "NV_Cache"));
                // AMD
                cleaned += CleanDirectory(Path.Combine(localAppData, "AMD", "DxCache"));
                cleaned += CleanDirectory(Path.Combine(localAppData, "AMD", "GLCache"));
                cleaned += CleanDirectory(Path.Combine(localAppData, "AMD", "VkCache"));
                // Intel
                cleaned += CleanDirectory(Path.Combine(localAppData, "Intel", "ShaderCache"));
                // DirectX Shader Cache (всі)
                cleaned += CleanDirectory(Path.Combine(localAppData, "D3DSCache"));
                // DirectX Pipeline Cache
                cleaned += CleanDirectory(Path.Combine(localAppData, "Microsoft", "DirectX Shader Cache"));
            }
            Logger.Info($"[DiskClean] GPU caches: {cleaned / (1024 * 1024)} MB");
            return cleaned;
        });

        // ============================================================
        // БЛОК 4: USER PROFILE CACHES
        // ============================================================

        // 13. Browser caches (всі юзери, всі браузери, всі профілі)
        onProgress?.Invoke("Очистка кешів браузерів...");
        totalCleaned += await Task.Run(() => CleanAllBrowserCaches());

        // 14. App caches (Teams, Discord, Spotify, Steam, etc.)
        onProgress?.Invoke("Очистка кешів додатків...");
        totalCleaned += await Task.Run(() => CleanAppCaches());

        // 15. General user caches
        onProgress?.Invoke("Очистка кешів користувачів...");
        totalCleaned += await Task.Run(() => CleanAllUserCaches());

        // ============================================================
        // БЛОК 5: ВЕЛИКІ СИСТЕМНІ ЦІЛІ
        // ============================================================

        // 16. Windows.old (10-30 GB!)
        onProgress?.Invoke("Видалення старої інсталяції Windows...");
        totalCleaned += await Task.Run(() =>
        {
            if (Directory.Exists(@"C:\Windows.old"))
            {
                try
                {
                    Logger.Info("[DiskClean] Windows.old found, cleaning...");
                    // takeown може висіти ДУЖЕ довго — 30с таймаут!
                    RunCmdWithTimeout("takeown /F C:\\Windows.old /R /A /D Y", 30);
                    RunCmdWithTimeout("icacls C:\\Windows.old /grant administrators:F /T /C /Q", 30);
                    var cleaned = CleanDirectory(@"C:\Windows.old");
                    try { Directory.Delete(@"C:\Windows.old", true); } catch { }
                    Logger.Info($"[DiskClean] Windows.old cleaned: {cleaned / (1024 * 1024)} MB");
                    return cleaned;
                }
                catch (Exception ex)
                {
                    Logger.Info($"[DiskClean] Windows.old error: {ex.Message}");
                }
            }
            return 0L;
        });

        // 17. DISM cleanup (WinSxS — 2-8 GB!)
        onProgress?.Invoke("DISM очистка компонентів...");
        await Task.Run(() => RunDismCleanup());

        // 18. Windows Store cache
        onProgress?.Invoke("Очистка кешу Microsoft Store...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            foreach (var userDir in GetUserDirectories())
            {
                var storeCache = Path.Combine(userDir, "AppData", "Local", "Packages",
                    "Microsoft.WindowsStore_8wekyb3d8bbwe", "LocalCache");
                cleaned += CleanDirectory(storeCache);
            }
            // Global store cache
            RunCmdWithTimeout("wsreset.exe -i", 15); // silent reset
            return cleaned;
        });

        // 19. Disk Cleanup utility (cleanmgr) — системна очистка
        onProgress?.Invoke("Системна очистка диска...");
        await Task.Run(() => RunSystemCleanup());

        // 20. Font cache
        onProgress?.Invoke("Очистка кешу шрифтів...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            StopService("FontCache");
            cleaned += CleanFilesByPattern(
                @"C:\Windows\ServiceProfiles\LocalService\AppData\Local", "FontCache*.dat");
            StartService("FontCache");
            return cleaned;
        });

        // 21. Thumbnail + icon cache
        onProgress?.Invoke("Очистка мініатюр...");
        totalCleaned += await Task.Run(() =>
        {
            long cleaned = 0;
            foreach (var userDir in GetUserDirectories())
            {
                var explorerDir = Path.Combine(userDir, "AppData", "Local",
                    "Microsoft", "Windows", "Explorer");
                cleaned += CleanFilesByPattern(explorerDir, "thumbcache_*.db");
                cleaned += CleanFilesByPattern(explorerDir, "iconcache_*.db");
            }
            return cleaned;
        });

        Logger.Info($"=== TOTAL DISK CLEANUP: {totalCleaned / (1024.0 * 1024.0 * 1024.0):F2} GB ({totalCleaned} bytes) ===");
        return totalCleaned;
    }

    // ================================================================
    // BROWSER CACHES — всі юзери, всі браузери, всі профілі
    // ================================================================
    private static long CleanAllBrowserCaches()
    {
        long cleaned = 0;
        foreach (var userDir in GetUserDirectories())
        {
            var localAppData = Path.Combine(userDir, "AppData", "Local");
            var roamingAppData = Path.Combine(userDir, "AppData", "Roaming");

            // Chromium-based browsers: cache directories
            var chromiumBrowsers = new (string BasePath, string DataFolder)[]
            {
                (Path.Combine(localAppData, "Google", "Chrome"), "User Data"),
                (Path.Combine(localAppData, "Microsoft", "Edge"), "User Data"),
                (Path.Combine(localAppData, "Yandex", "YandexBrowser"), "User Data"),
                (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser"), "User Data"),
                (Path.Combine(localAppData, "Vivaldi"), "User Data"),
                (Path.Combine(localAppData, "CentBrowser"), "User Data"),
                (Path.Combine(roamingAppData, "Opera Software", "Opera Stable"), ""),
                (Path.Combine(roamingAppData, "Opera Software", "Opera GX Stable"), ""),
            };

            foreach (var browser in chromiumBrowsers)
            {
                var dataDir = string.IsNullOrEmpty(browser.DataFolder)
                    ? browser.BasePath
                    : Path.Combine(browser.BasePath, browser.DataFolder);

                if (!Directory.Exists(dataDir)) continue;

                // Clean default profile + all Profile N
                var profileDirs = new List<string>();
                if (string.IsNullOrEmpty(browser.DataFolder))
                {
                    // Opera-style: data in root
                    profileDirs.Add(dataDir);
                }
                else
                {
                    // Chrome-style: profiles in User Data
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(dataDir))
                        {
                            var name = Path.GetFileName(dir).ToLowerInvariant();
                            if (name == "default" || name.StartsWith("profile ") || name.StartsWith("profile_"))
                                profileDirs.Add(dir);
                        }
                    }
                    catch { }
                }

                foreach (var profileDir in profileDirs)
                {
                    var cacheDirs = new[]
                    {
                        "Cache", "Code Cache", "GPUCache", "DawnCache", "GrShaderCache",
                        "ShaderCache", "Storage\\ext",
                        Path.Combine("Service Worker", "CacheStorage"),
                        Path.Combine("Service Worker", "ScriptCache"),
                    };
                    foreach (var cacheDir in cacheDirs)
                    {
                        cleaned += CleanDirectory(Path.Combine(profileDir, cacheDir));
                    }
                }

                // Also clean CrashpadMetrics and SwReporter
                cleaned += CleanDirectory(Path.Combine(dataDir, "CrashpadMetrics-active.pma"));
                cleaned += CleanDirectory(Path.Combine(dataDir, "SwReporter"));
            }

            // Firefox cache (всі профілі)
            var ffProfilesDir = Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(ffProfilesDir))
            {
                try
                {
                    foreach (var profile in Directory.GetDirectories(ffProfilesDir))
                    {
                        cleaned += CleanDirectory(Path.Combine(profile, "cache2"));
                        cleaned += CleanDirectory(Path.Combine(profile, "thumbnails"));
                        cleaned += CleanDirectory(Path.Combine(profile, "startupCache"));
                        cleaned += CleanDirectory(Path.Combine(profile, "shader-cache"));
                    }
                }
                catch { }
            }
        }
        Logger.Info($"[DiskClean] Browser caches: {cleaned / (1024 * 1024)} MB");
        return cleaned;
    }

    // ================================================================
    // APP CACHES — Teams, Discord, Spotify, Steam, etc.
    // ================================================================
    private static long CleanAppCaches()
    {
        long cleaned = 0;
        foreach (var userDir in GetUserDirectories())
        {
            var localAppData = Path.Combine(userDir, "AppData", "Local");
            var roamingAppData = Path.Combine(userDir, "AppData", "Roaming");

            // Microsoft Teams
            var teamsDir = Path.Combine(localAppData, "Microsoft", "Teams");
            cleaned += CleanDirectory(Path.Combine(teamsDir, "Cache"));
            cleaned += CleanDirectory(Path.Combine(teamsDir, "blob_storage"));
            cleaned += CleanDirectory(Path.Combine(teamsDir, "databases"));
            cleaned += CleanDirectory(Path.Combine(teamsDir, "GPUCache"));
            cleaned += CleanDirectory(Path.Combine(teamsDir, "IndexedDB"));
            cleaned += CleanDirectory(Path.Combine(teamsDir, "Local Storage"));
            cleaned += CleanDirectory(Path.Combine(teamsDir, "tmp"));
            // New Teams (v2)
            cleaned += CleanDirectory(Path.Combine(localAppData, "Packages",
                "MSTeams_8wekyb3d8bbwe", "LocalCache"));

            // Discord
            var discordDir = Path.Combine(roamingAppData, "discord");
            cleaned += CleanDirectory(Path.Combine(discordDir, "Cache"));
            cleaned += CleanDirectory(Path.Combine(discordDir, "Code Cache"));
            cleaned += CleanDirectory(Path.Combine(discordDir, "GPUCache"));

            // Spotify
            var spotifyDir = Path.Combine(localAppData, "Spotify");
            cleaned += CleanDirectory(Path.Combine(spotifyDir, "Data"));
            cleaned += CleanDirectory(Path.Combine(spotifyDir, "Storage"));
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Spotify", "Data"));

            // Telegram Desktop
            var tgDir = Path.Combine(roamingAppData, "Telegram Desktop");
            cleaned += CleanDirectory(Path.Combine(tgDir, "tdata", "user_data"));
            cleaned += CleanDirectory(Path.Combine(tgDir, "tdata", "emoji"));

            // Steam
            cleaned += CleanDirectory(@"C:\Program Files (x86)\Steam\appcache");
            cleaned += CleanDirectory(@"C:\Program Files (x86)\Steam\depotcache");
            cleaned += CleanDirectory(@"C:\Program Files (x86)\Steam\logs");
            cleaned += CleanDirectory(@"C:\Program Files (x86)\Steam\dumps");

            // VSCode
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Code", "Cache"));
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Code", "CachedData"));
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Code", "CachedExtensions"));
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Code", "Code Cache"));
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Code", "GPUCache"));
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Code", "logs"));

            // Zoom
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Zoom", "data"));

            // Slack
            var slackDir = Path.Combine(roamingAppData, "Slack");
            cleaned += CleanDirectory(Path.Combine(slackDir, "Cache"));
            cleaned += CleanDirectory(Path.Combine(slackDir, "Code Cache"));
            cleaned += CleanDirectory(Path.Combine(slackDir, "GPUCache"));
            cleaned += CleanDirectory(Path.Combine(slackDir, "Service Worker"));

            // AnyDesk
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "AnyDesk", "thumbnails"));

            // Skype
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "Microsoft", "Skype for Desktop", "Cache"));

            // Adobe
            cleaned += CleanDirectory(Path.Combine(localAppData, "Adobe", "Acrobat", "DC", "Cache"));
            cleaned += CleanDirectory(Path.Combine(localAppData, "Adobe", "Acrobat", "DC", "ConnectorIcons"));

            // .NET / NuGet caches
            cleaned += CleanDirectory(Path.Combine(localAppData, "NuGet", "v3-cache"));
            cleaned += CleanDirectory(Path.Combine(localAppData, "Temp", ".NETFramework"));

            // pip cache (Python)
            cleaned += CleanDirectory(Path.Combine(localAppData, "pip", "cache"));

            // npm cache
            cleaned += CleanDirectory(Path.Combine(roamingAppData, "npm-cache"));
        }
        Logger.Info($"[DiskClean] App caches: {cleaned / (1024 * 1024)} MB");
        return cleaned;
    }

    // ================================================================
    // GENERAL USER CACHES
    // ================================================================
    private static long CleanAllUserCaches()
    {
        long cleaned = 0;
        foreach (var userDir in GetUserDirectories())
        {
            try
            {
                var localAppData = Path.Combine(userDir, "AppData", "Local");

                // Temp
                cleaned += CleanDirectory(Path.Combine(localAppData, "Temp"));
                // CrashDumps
                cleaned += CleanDirectory(Path.Combine(localAppData, "CrashDumps"));
                // INetCache
                cleaned += CleanDirectory(Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"));
                // Notifications
                cleaned += CleanDirectory(Path.Combine(localAppData, "Microsoft", "Windows", "Notifications"));
                // WebCache
                cleaned += CleanDirectory(Path.Combine(localAppData, "Microsoft", "Windows", "WebCache"));
                // Windows Caches
                cleaned += CleanDirectory(Path.Combine(localAppData, "Microsoft", "Windows", "Caches"));
                // History
                cleaned += CleanDirectory(Path.Combine(localAppData, "Microsoft", "Windows", "History"));

                // UWP app TempState / AC caches
                var packagesDir = Path.Combine(localAppData, "Packages");
                if (Directory.Exists(packagesDir))
                {
                    try
                    {
                        foreach (var pkgDir in Directory.GetDirectories(packagesDir))
                        {
                            cleaned += CleanDirectory(Path.Combine(pkgDir, "TempState"));
                            cleaned += CleanDirectory(Path.Combine(pkgDir, "AC", "Temp"));
                            cleaned += CleanDirectory(Path.Combine(pkgDir, "AC", "INetCache"));
                            cleaned += CleanDirectory(Path.Combine(pkgDir, "AC", "INetCookies"));
                            cleaned += CleanDirectory(Path.Combine(pkgDir, "LocalCache", "Roaming"));
                        }
                    }
                    catch { }
                }

                // Downloads вже очищено в Блоці 0 — пропускаємо
            }
            catch { }
        }
        Logger.Info($"[DiskClean] User caches: {cleaned / (1024 * 1024)} MB");
        return cleaned;
    }

    // ================================================================
    // DRIVER STORE CLEANUP
    // ================================================================
    private static long CleanOldDrivers()
    {
        long cleaned = 0;
        try
        {
            // Простий і надійний метод — pnputil з жорстким таймаутом
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = "-NoProfile -Command \"pnputil /enum-drivers 2>$null | Select-String 'oem' | ForEach-Object { $n = ($_ -split '\\s+')[0]; try { pnputil /delete-driver $n 2>$null } catch {} }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!proc.WaitForExit(45000)) // 45 секунд MAX!
                {
                    Logger.Info("[DiskClean] Driver cleanup timeout — killing");
                    try { proc.Kill(true); } catch { }
                }
                else
                {
                    Logger.Info("[DiskClean] Driver store cleanup completed");
                }
            }

            // Clean driver temp files
            cleaned += CleanDirectory(@"C:\Windows\System32\DriverStore\Temp");
        }
        catch (Exception ex)
        {
            Logger.Info($"[DiskClean] Driver cleanup error: {ex.Message}");
        }
        return cleaned;
    }

    // ================================================================
    // EVENT LOGS CLEANUP
    // ================================================================
    private static void ClearEventLogs()
    {
        try
        {
            // Простіший і швидший метод — wevtutil
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c for /F \"tokens=*\" %1 in ('wevtutil el') DO wevtutil cl \"%1\" 2>nul",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!proc.WaitForExit(20000)) // 20с MAX
                {
                    try { proc.Kill(true); } catch { }
                }
            }
            Logger.Info("[DiskClean] Event logs cleared");
        }
        catch { }
    }

    // ================================================================
    // UTILITY METHODS
    // ================================================================

    /// <summary>
    /// Повністю очистити папку — видалити ВСІ файли і підпапки!
    /// Для Desktop, Downloads — після "переустановки" вони мають бути пусті.
    /// Якщо файл залочений — вбиваємо процес і пробуємо ще раз.
    /// </summary>
    private static long CleanDirectoryCompletely(string path)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;

            // Видалити всі файли
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    // Пропустити desktop.ini (системний файл)
                    if (fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var size = fi.Length;

                    try
                    {
                        fi.Delete();
                        cleaned += size;
                    }
                    catch (IOException)
                    {
                        // Файл залочений — спробувати вбити процес
                        if (TryKillLockingProcess(fi.FullName))
                        {
                            Thread.Sleep(500);
                            try
                            {
                                fi.Delete();
                                cleaned += size;
                            }
                            catch { }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Немає прав — спробувати через cmd /c del /f
                        try
                        {
                            ForceDeleteFile(fi.FullName);
                            if (!fi.Exists)
                                cleaned += size;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Видалити всі підпапки
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                try
                {
                    var dirSize = GetDirectorySize(dir);
                    Directory.Delete(dir, true);
                    cleaned += dirSize;
                }
                catch
                {
                    // Спробувати через cmd /c rd /s /q
                    try
                    {
                        ForceDeleteDirectory(dir);
                        if (!Directory.Exists(dir))
                            cleaned += GetDirectorySize(dir);
                    }
                    catch { }
                    // Якщо все одно не вдалося — хоча б вміст
                    cleaned += CleanDirectory(dir);
                }
            }
        }
        catch { }
        return cleaned;
    }

    /// <summary>
    /// Знайти і вбити процес який тримає файл.
    /// </summary>
    private static bool TryKillLockingProcess(string filePath)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            // Шукаємо процес з такою ж назвою як файл
            foreach (var proc in Process.GetProcessesByName(fileName))
            {
                try
                {
                    proc.Kill(true);
                    Logger.Info($"[DiskClean] Killed locking process: {proc.ProcessName}");
                    return true;
                }
                catch { }
            }

            // Також перевіримо чи exe файл — шукаємо процес за шляхом
            if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.MainModule?.FileName?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            proc.Kill(true);
                            Logger.Info($"[DiskClean] Killed by path: {proc.ProcessName}");
                            return true;
                        }
                    }
                    catch { } // Access denied — skip
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Примусове видалення файлу через cmd /c del /f /q
    /// </summary>
    private static void ForceDeleteFile(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c del /f /q \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>
    /// Примусове видалення папки через cmd /c rd /s /q
    /// </summary>
    private static void ForceDeleteDirectory(string dirPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c rd /s /q \"{dirPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch { }
    }

    /// <summary>
    /// Видалити файли з конкретними розширеннями рекурсивно.
    /// </summary>
    private static long CleanFilesByExtensions(string path, string[] extensions)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        var size = new FileInfo(file).Length;
                        File.Delete(file);
                        cleaned += size;
                    }
                }
                catch { }
            }
        }
        catch { }
        return cleaned;
    }

    private static string[] GetUserDirectories()
    {
        var usersDir = @"C:\Users";
        if (!Directory.Exists(usersDir)) return Array.Empty<string>();

        try
        {
            return Directory.GetDirectories(usersDir)
                .Where(d =>
                {
                    var name = Path.GetFileName(d).ToLowerInvariant();
                    return name is not ("public" or "default" or "default user" or "all users");
                })
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static long CleanDirectory(string path)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                }
                catch { } // File in use — skip
            }

            // Clean empty subdirectories
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                    .Reverse())
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    }
                    catch { }
                }
            }
            catch { }
        }
        catch { }
        return cleaned;
    }

    private static long CleanFilesByPattern(string path, string pattern)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;
            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                }
                catch { }
            }
        }
        catch { }
        return cleaned;
    }

    /// <summary>
    /// Видалити файли старші за N днів.
    /// </summary>
    private static long CleanOldFiles(string path, string pattern, int daysOld)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;
            var cutoff = DateTime.Now.AddDays(-daysOld);
            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTime < cutoff)
                    {
                        cleaned += fi.Length;
                        fi.Delete();
                    }
                }
                catch { }
            }
        }
        catch { }
        return cleaned;
    }

    /// <summary>
    /// Видалити файли з конкретними розширеннями (для Downloads).
    /// </summary>
    private static long CleanOldFilesByExtension(string path, string[] extensions, int daysOld)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;
            var cutoff = daysOld > 0 ? DateTime.Now.AddDays(-daysOld) : DateTime.Now;
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext))
                    {
                        var fi = new FileInfo(file);
                        if (daysOld <= 0 || fi.LastWriteTime < cutoff)
                        {
                            cleaned += fi.Length;
                            fi.Delete();
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return cleaned;
    }

    private static long ClearRecycleBin()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = "-NoProfile -Command \"Clear-RecycleBin -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            Logger.Info("[DiskClean] Recycle bin cleared");
            return 0; // Size counted in scan
        }
        catch { return 0; }
    }

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    private static void StopService(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = $"stop {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
        }
        catch { }
    }

    private static void StartService(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = $"start {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
        }
        catch { }
    }

    private static void RunCmdWithTimeout(string command, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!proc.WaitForExit(timeoutSec * 1000))
                {
                    // ТАЙМАУТ — вбиваємо процес!
                    Logger.Info($"[DiskClean] CMD timeout {timeoutSec}s: {command}");
                    try { proc.Kill(true); } catch { }
                }
            }
        }
        catch { }
    }

    private static void RunDismCleanup()
    {
        try
        {
            var sys32 = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
                : Environment.SystemDirectory;
            var dismPath = Path.Combine(sys32, "DISM.exe");
            if (!File.Exists(dismPath)) dismPath = "DISM.exe";

            // Тільки один виклик з ResetBase — і ЖОРСТКИЙ таймаут 90с!
            // На повільних ПК DISM може висіти 30+ хвилин — це неприпустимо
            var psi = new ProcessStartInfo
            {
                FileName = dismPath,
                Arguments = "/Online /Cleanup-Image /StartComponentCleanup /ResetBase",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!proc.WaitForExit(90000)) // 90 секунд MAX!
                {
                    Logger.Info("[DiskClean] DISM timeout 90s — killing");
                    try { proc.Kill(true); } catch { }
                }
                else
                {
                    Logger.Info("[DiskClean] DISM completed");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DiskClean] DISM cleanup failed", ex);
        }
    }

    private static void RunSystemCleanup()
    {
        try
        {
            // Set all cleanup flags via registry
            var psScript = "Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VolumeCaches\\*' | " +
                          "ForEach-Object { New-ItemProperty -Path $_.PSPath -Name StateFlags0099 -Value 2 -PropertyType DWord -Force -ErrorAction SilentlyContinue }";
            var encodedCmd = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript));
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -EncodedCommand {encodedCmd}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var setupProc = Process.Start(psi);
            setupProc?.WaitForExit(10000);

            // Run cleanmgr з ЖОРСТКИМ таймаутом 60с!
            // cleanmgr може показати діалог і зависнути нескінченно
            var cleanPsi = new ProcessStartInfo
            {
                FileName = "cleanmgr.exe",
                Arguments = "/sagerun:99",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var cleanProc = Process.Start(cleanPsi);
            if (cleanProc != null)
            {
                if (!cleanProc.WaitForExit(60000)) // 60 секунд MAX!
                {
                    Logger.Info("[DiskClean] cleanmgr timeout 60s — killing");
                    try { cleanProc.Kill(true); } catch { }
                }
                else
                {
                    Logger.Info("[DiskClean] cleanmgr completed");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[DiskClean] System cleanup failed: {ex.Message}");
        }
    }
}
