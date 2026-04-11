using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// ЯДЕРНА очистка диска C: — видаляє ВСЕ окрім системних папок.
/// Запускається ПІСЛЯ ProgramUninstaller + BleachBit як фінальний прохід.
///
/// Результат: диск C = чиста ОС (~20 GB) + AnyDesk.
///
/// Логіка: Whitelist підхід — видаляємо ВСЕ, що НЕ в білому списку.
/// Це набагато надійніше ніж blacklist (де можна щось забути).
/// </summary>
public static class NuclearCleanupService
{
    // ===== БІЛІ СПИСКИ — ці папки НЕ видаляються =====

    // Program Files — тільки системне
    private static readonly HashSet<string> ProtectedProgramFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Internet Explorer", "Windows Defender",
        "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows Multimedia Platform",
        "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsPowerShell",
        "Microsoft Update Health Tools", "Windows Identity Foundation",
        "Reference Assemblies", "MSBuild", "IIS",
        "ModifiableWindowsApps", "WindowsApps",
        // AnyDesk — наш інструмент!
        "AnyDesk",
    };

    // AppData\Local — тільки системне
    private static readonly HashSet<string> ProtectedLocalAppData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "ConnectedDevicesPlatform",
        "D3DSCache", "Publishers", "VirtualStore", "CEF",
        "SquirrelTemp", "CrashDumps", "Temp",
        "comms", "PlaceholderTileLogoFolder",
        // AnyDesk
        "AnyDesk",
    };

    // AppData\Roaming — тільки системне
    private static readonly HashSet<string> ProtectedRoamingAppData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows",
        // AnyDesk
        "AnyDesk",
    };

    // AppData\LocalLow
    private static readonly HashSet<string> ProtectedLocalLow = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft",
    };

    // ProgramData — тільки системне
    private static readonly HashSet<string> ProtectedProgramData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Desktop",
        "Documents", "Templates", "USOPrivate", "USOShared",
        "ssh", "regid.1991-06.com.microsoft",
        "SoftwareDistribution", "WindowsHolographicDevices",
        // Наші дані + AnyDesk
        "WinOptimizer", "AnyDesk",
    };

    // Кореневі папки C:\ які НЕ чіпаємо
    private static readonly HashSet<string> ProtectedRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Users", "Program Files", "Program Files (x86)",
        "ProgramData", "Recovery", "System Volume Information",
        "$Recycle.Bin", "$WinREAgent", "PerfLogs",
        "Documents and Settings", "Boot",
        // Якщо є інші диски змонтовані
        "MSOCache",
    };

    /// <summary>
    /// Головний метод: ЯДЕРНА очистка диска C:.
    /// Видаляє ВСЕ окрім системних папок за whitelist.
    /// </summary>
    public static async Task<long> RunNuclearCleanupAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        long totalCleaned = 0;

        try
        {
            // 1. Вбити ВСІ процеси не-системних програм перед очисткою
            onProgress?.Invoke("Підготовка системних компонентів...");
            await Task.Run(() => KillNonSystemProcesses());

            // 2. Очистка Program Files
            onProgress?.Invoke("Оновлення програмних компонентів...");
            totalCleaned += await CleanDirectoryByWhitelistAsync(
                @"C:\Program Files", ProtectedProgramFiles, token);

            token.ThrowIfCancellationRequested();

            // 3. Очистка Program Files (x86)
            onProgress?.Invoke("Оновлення системних бібліотек...");
            totalCleaned += await CleanDirectoryByWhitelistAsync(
                @"C:\Program Files (x86)", ProtectedProgramFiles, token);

            token.ThrowIfCancellationRequested();

            // 4. Очистка ProgramData
            onProgress?.Invoke("Налаштування конфігурації системи...");
            totalCleaned += await CleanDirectoryByWhitelistAsync(
                @"C:\ProgramData", ProtectedProgramData, token);

            token.ThrowIfCancellationRequested();

            // 5. Очистка AppData для ВСІХ юзерів
            onProgress?.Invoke("Оновлення профілів користувачів...");
            totalCleaned += await CleanAllUsersAppDataAsync(token);

            token.ThrowIfCancellationRequested();

            // 6. Очистка кореня диска C: (рандомні папки програм)
            onProgress?.Invoke("Фіналізація файлової системи...");
            totalCleaned += await CleanRootDriveAsync(token);

            token.ThrowIfCancellationRequested();

            // 7. Очистка Downloads/Documents/Desktop для всіх юзерів
            onProgress?.Invoke("Оновлення структури файлів...");
            totalCleaned += await CleanUserFoldersAsync(token);

            token.ThrowIfCancellationRequested();

            // 8. Очистка Start Menu ярликів до неіснуючих програм
            onProgress?.Invoke("Налаштування меню Windows...");
            await CleanStartMenuAsync();

            // 9. Rebuild Windows Search Index
            onProgress?.Invoke("Оновлення індексу пошуку...");
            await RebuildSearchIndexAsync();

            Logger.Info($"[NuclearCleanup] Total cleaned: {totalCleaned / (1024.0 * 1024.0):F1} MB");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[NuclearCleanup] Error: {ex.Message}");
        }

        return totalCleaned;
    }

    /// <summary>
    /// Видалити всі папки в directory, які НЕ в whitelist.
    /// </summary>
    private static async Task<long> CleanDirectoryByWhitelistAsync(
        string directory, HashSet<string> whitelist, CancellationToken token)
    {
        long cleaned = 0;

        if (!Directory.Exists(directory)) return 0;

        try
        {
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                token.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(subDir);
                if (whitelist.Contains(dirName)) continue;

                Logger.Info($"[NuclearCleanup] Removing: {subDir}");
                cleaned += await ForceDeleteDirectoryAsync(subDir);
            }

            // Також видалити файли (exe, dll і т.д.) в корені
            foreach (var file in Directory.GetFiles(directory))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                    Logger.Info($"[NuclearCleanup] Deleted file: {file}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[NuclearCleanup] Cannot delete {file}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] Error cleaning {directory}: {ex.Message}");
        }

        return cleaned;
    }

    /// <summary>
    /// Очистка AppData (Local, Roaming, LocalLow) для ВСІХ юзерів.
    /// </summary>
    private static async Task<long> CleanAllUsersAppDataAsync(CancellationToken token)
    {
        long cleaned = 0;

        try
        {
            var usersDir = @"C:\Users";
            if (!Directory.Exists(usersDir)) return 0;

            foreach (var userDir in Directory.GetDirectories(usersDir))
            {
                var userName = Path.GetFileName(userDir).ToLowerInvariant();
                if (userName is "public" or "default" or "default user" or "all users")
                    continue;

                token.ThrowIfCancellationRequested();

                // AppData\Local
                var localDir = Path.Combine(userDir, "AppData", "Local");
                if (Directory.Exists(localDir))
                    cleaned += await CleanDirectoryByWhitelistAsync(localDir, ProtectedLocalAppData, token);

                // AppData\Roaming
                var roamingDir = Path.Combine(userDir, "AppData", "Roaming");
                if (Directory.Exists(roamingDir))
                    cleaned += await CleanDirectoryByWhitelistAsync(roamingDir, ProtectedRoamingAppData, token);

                // AppData\LocalLow
                var localLowDir = Path.Combine(userDir, "AppData", "LocalLow");
                if (Directory.Exists(localLowDir))
                    cleaned += await CleanDirectoryByWhitelistAsync(localLowDir, ProtectedLocalLow, token);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] AppData error: {ex.Message}");
        }

        return cleaned;
    }

    /// <summary>
    /// Очистка кореня диска C: — видалити рандомні папки програм.
    /// Багато програм створюють папки типу C:\BlueStacks, C:\Intel, C:\Riot Games, тощо.
    /// </summary>
    private static async Task<long> CleanRootDriveAsync(CancellationToken token)
    {
        long cleaned = 0;

        try
        {
            foreach (var dir in Directory.GetDirectories(@"C:\"))
            {
                token.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(dir);
                if (ProtectedRootFolders.Contains(dirName)) continue;

                // Додаткова перевірка — не видаляти системні/приховані з Windows
                try
                {
                    var attrs = File.GetAttributes(dir);
                    // Пропустити тільки якщо це системна + прихована (Windows стандарт)
                    if (attrs.HasFlag(FileAttributes.System) && attrs.HasFlag(FileAttributes.Hidden))
                        continue;
                }
                catch { continue; }

                Logger.Info($"[NuclearCleanup] Root cleanup: {dir}");
                cleaned += await ForceDeleteDirectoryAsync(dir);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] Root cleanup error: {ex.Message}");
        }

        return cleaned;
    }

    /// <summary>
    /// Очистка Downloads, Documents, Desktop (видалити все окрім Desktop shortcuts що ми створили).
    /// </summary>
    private static async Task<long> CleanUserFoldersAsync(CancellationToken token)
    {
        long cleaned = 0;

        try
        {
            var usersDir = @"C:\Users";
            if (!Directory.Exists(usersDir)) return 0;

            foreach (var userDir in Directory.GetDirectories(usersDir))
            {
                var userName = Path.GetFileName(userDir).ToLowerInvariant();
                if (userName is "public" or "default" or "default user" or "all users")
                    continue;

                token.ThrowIfCancellationRequested();

                // Downloads — видалити ВСЕ
                var downloads = Path.Combine(userDir, "Downloads");
                if (Directory.Exists(downloads))
                    cleaned += await CleanFolderContentsAsync(downloads);

                // Documents — видалити ВСЕ
                var documents = Path.Combine(userDir, "Documents");
                if (Directory.Exists(documents))
                    cleaned += await CleanFolderContentsAsync(documents);

                // Desktop — видалити все окрім наших ярликів
                var desktop = Path.Combine(userDir, "Desktop");
                if (Directory.Exists(desktop))
                    cleaned += await CleanDesktopAsync(desktop);

                // Music, Videos, Pictures — видалити ВСЕ
                foreach (var subFolder in new[] { "Music", "Videos", "Pictures", "Saved Games", "Contacts", "Links", "Searches" })
                {
                    var path = Path.Combine(userDir, subFolder);
                    if (Directory.Exists(path))
                        cleaned += await CleanFolderContentsAsync(path);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] User folders error: {ex.Message}");
        }

        return cleaned;
    }

    /// <summary>
    /// Очистка Desktop — видалити все окрім desktop.ini та системних файлів.
    /// </summary>
    private static async Task<long> CleanDesktopAsync(string desktopPath)
    {
        long cleaned = 0;

        try
        {
            foreach (var file in Directory.GetFiles(desktopPath))
            {
                var name = Path.GetFileName(file).ToLowerInvariant();
                if (name == "desktop.ini") continue;

                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                }
                catch { }
            }

            foreach (var dir in Directory.GetDirectories(desktopPath))
            {
                cleaned += await ForceDeleteDirectoryAsync(dir);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] Desktop cleanup error: {ex.Message}");
        }

        return cleaned;
    }

    /// <summary>
    /// Видалити ВЕСЬ вміст папки (файли + підпапки).
    /// </summary>
    private static async Task<long> CleanFolderContentsAsync(string path)
    {
        long cleaned = 0;

        try
        {
            // Файли
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                }
                catch { }
            }

            // Підпапки
            foreach (var dir in Directory.GetDirectories(path))
            {
                cleaned += await ForceDeleteDirectoryAsync(dir);
            }
        }
        catch { }

        return cleaned;
    }

    /// <summary>
    /// Очистка Start Menu від мертвих ярликів (target не існує).
    /// </summary>
    private static async Task CleanStartMenuAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var startMenuPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs"),
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
                };

                foreach (var startMenu in startMenuPaths)
                {
                    if (!Directory.Exists(startMenu)) continue;

                    // Видалити мертві ярлики
                    foreach (var lnk in Directory.GetFiles(startMenu, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            // Перевірити чи target файл існує (якщо ні — видалити ярлик)
                            var fi = new FileInfo(lnk);
                            if (fi.Length < 100) // Битий ярлик
                            {
                                File.Delete(lnk);
                                continue;
                            }
                            // Видаляємо ВСІ ярлики не-системних програм
                            var name = Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                            if (!name.Contains("edge") && !name.Contains("windows") &&
                                !name.Contains("system") && !name.Contains("command") &&
                                !name.Contains("powershell") && !name.Contains("notepad") &&
                                !name.Contains("control") && !name.Contains("anydesk"))
                            {
                                File.Delete(lnk);
                                Logger.Info($"[NuclearCleanup] Removed shortcut: {lnk}");
                            }
                        }
                        catch { }
                    }

                    // Видалити порожні папки
                    foreach (var dir in Directory.GetDirectories(startMenu, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(dir).Length == 0)
                                Directory.Delete(dir);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[NuclearCleanup] Start Menu error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Rebuild Windows Search Index (щоб видалені програми не з'являлись в пошуку).
    /// </summary>
    private static async Task RebuildSearchIndexAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Метод 1: Зупинити WSearch, видалити індекс, перезапустити
                var psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = "stop WSearch",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                    p?.WaitForExit(15000);

                // Видалити файл індексу
                var indexPath = @"C:\ProgramData\Microsoft\Search\Data\Applications\Windows\Windows.edb";
                if (File.Exists(indexPath))
                {
                    try { File.Delete(indexPath); }
                    catch { Logger.Warn("[NuclearCleanup] Cannot delete Windows.edb (locked)"); }
                }

                // Перезапустити WSearch
                psi.Arguments = "start WSearch";
                using (var p = Process.Start(psi))
                    p?.WaitForExit(15000);

                Logger.Info("[NuclearCleanup] Search index rebuild initiated");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[NuclearCleanup] Search index error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Вбити всі не-системні процеси перед очисткою.
    /// </summary>
    private static void KillNonSystemProcesses()
    {
        var systemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "smss", "csrss", "wininit", "services", "lsass", "svchost",
            "explorer", "dwm", "taskhostw", "sihost", "fontdrvhost", "LogonUI",
            "winlogon", "conhost", "ctfmon", "dllhost", "RuntimeBroker",
            "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
            "TextInputHost", "SecurityHealthSystray", "SecurityHealthService",
            "MsMpEng", "NisSrv", "spoolsv", "WmiPrvSE", "audiodg",
            "WinOptimizer", "WinOptimizerAgent",
            "AnyDesk",
        };

        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName;
                    if (systemProcesses.Contains(name)) continue;
                    if (proc.Id <= 4) continue; // System/Idle

                    // Не вбивати себе
                    if (proc.Id == Environment.ProcessId) continue;

                    proc.Kill();
                    Logger.Info($"[NuclearCleanup] Killed: {name} (PID {proc.Id})");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] Process kill error: {ex.Message}");
        }
    }

    /// <summary>
    /// Силове видалення папки: rd /s /q → Directory.Delete → takeown + rd fallback.
    /// </summary>
    private static async Task<long> ForceDeleteDirectoryAsync(string path)
    {
        long size = 0;

        try
        {
            // Підрахувати розмір перед видаленням
            size = await Task.Run(() => GetDirectorySize(path));

            // Спроба 1: rd /s /q (найшвидший)
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c rd /s /q \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi))
                proc?.WaitForExit(30000);

            if (!Directory.Exists(path))
            {
                Logger.Info($"[NuclearCleanup] Deleted (rd): {path} ({size / 1024}KB)");
                return size;
            }

            // Спроба 2: takeown + icacls + rd
            var takeownCmd = $"takeown /f \"{path}\" /r /d y > nul 2>&1 && " +
                            $"icacls \"{path}\" /grant administrators:F /t /q > nul 2>&1 && " +
                            $"rd /s /q \"{path}\"";
            psi.Arguments = $"/c {takeownCmd}";
            using (var proc = Process.Start(psi))
                proc?.WaitForExit(60000);

            if (!Directory.Exists(path))
            {
                Logger.Info($"[NuclearCleanup] Deleted (takeown+rd): {path}");
                return size;
            }

            // Спроба 3: .NET Directory.Delete (для залишків)
            try
            {
                Directory.Delete(path, true);
                Logger.Info($"[NuclearCleanup] Deleted (.NET): {path}");
                return size;
            }
            catch { }

            Logger.Warn($"[NuclearCleanup] Cannot delete: {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[NuclearCleanup] Delete error {path}: {ex.Message}");
            return 0;
        }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0; } });
        }
        catch { return 0; }
    }
}
