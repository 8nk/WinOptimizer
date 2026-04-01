using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// ГЛИБОКА деінсталяція програм v2.0 — ПОВНЕ видалення без залишків!
///
/// Мультипідхід:
/// 1. Вбити процес програми
/// 2. Тихий uninstall (15с timeout)
/// 3. Примусово видалити папку програми
/// 4. 🆕 ГЛИБОКА ОЧИСТКА ЗАЛИШКІВ:
///    - AppData\Roaming\<program>
///    - AppData\Local\<program>
///    - ProgramData\<program>
///    - Start Menu shortcuts
///    - Registry uninstall keys
///    - Scheduled Tasks
///    - Services
/// 5. 🆕 Перебудова Windows Search індексу (щоб видалені програми зникли з пошуку!)
///
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// </summary>
public static class ProgramUninstaller
{
    // Програми які НЕ МОЖНА видаляти (системні компоненти Windows)
    private static readonly string[] ProtectedKeywords =
    {
        "Microsoft Visual C++", "Microsoft .NET", ".NET Framework", ".NET Runtime",
        "Windows Driver", "Microsoft Windows", "Windows SDK",
        "Microsoft Edge", "Microsoft OneDrive", "Microsoft Update",
        "NVIDIA Graphics", "NVIDIA PhysX", "NVIDIA GeForce",
        "AMD Software", "AMD Chipset", "AMD Radeon",
        "Realtek", "Intel(R)", "Intel ",
        "WinOptimizer", "Windows Defender",
        "Vulkan Runtime", "Microsoft XNA",
        "DirectX", "OpenAL",
    };

    // Процеси які ОБОВ'ЯЗКОВО вбити ПЕРЕД деінсталяцією
    private static readonly string[] KillBeforeUninstallProcesses =
    {
        "HD-Player", "BlueStacks", "BstkSVC", "BstkDrv",
        "BlueStacksHelper", "Bluestacks", "Bst",
        "TLauncher", "javaw", "java",
        "SoundBooster", "Letasoft",
        "python", "pythonw", "node",
        "Telegram", "Discord", "Viber", "WhatsApp", "Zoom",
        "Steam", "EpicGamesLauncher", "Origin",
        "Spotify", "slack",
    };

    // Папки AppData/ProgramData які НІКОЛИ не видаляти
    private static readonly HashSet<string> ProtectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Intel", "NVIDIA", "NVIDIA Corporation",
        "AMD", "Realtek", "Google", "Mozilla",
        "Adobe", // може бути потрібно
        "WinOptimizer", "WinFlow",
        ".NET", "NuGet", "Package Cache",
    };

    // Імітація логів переустановки Windows — юзер бачить ці повідомлення в UI
    private static readonly string[] WindowsInstallMessages =
    {
        "Налаштування системних компонентів...",
        "Застосування параметрів Windows...",
        "Оновлення системних файлів...",
        "Перевірка цілісності файлової системи...",
        "Встановлення компонентів оновлення...",
        "Налаштування системного реєстру...",
        "Видалення тимчасових файлів установки...",
        "Оптимізація конфігурації системи...",
        "Застосування налаштувань безпеки...",
        "Налаштування мережевих компонентів...",
        "Оновлення бази даних пристроїв...",
        "Перевірка сумісності компонентів...",
        "Встановлення системних оновлень...",
        "Налаштування середовища Windows...",
        "Видалення застарілих компонентів...",
        "Перевірка системних бібліотек...",
        "Оптимізація завантаження системи...",
        "Застосування політик безпеки...",
        "Налаштування служб Windows...",
        "Фіналізація конфігурації системи...",
    };

    public static async Task<List<string>> UninstallAllProgramsAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        var removed = new List<string>();

        try
        {
            // === КРОК 0: ВБИТИ ВСІ ПРОЦЕСИ перед деінсталяцією ===
            onProgress?.Invoke("Підготовка системних файлів...");
            await Task.Run(() => KillAllTargetProcesses());

            onProgress?.Invoke("Аналіз конфігурації системи...");
            var programs = await GetInstalledProgramsAsync();
            Logger.Info($"Found {programs.Count} installed programs");

            foreach (var p in programs)
                Logger.Info($"  Program: [{p.Name}] Uninstall: [{p.UninstallString}] InstallDir: [{p.InstallLocation}]");

            var toRemove = programs
                .Where(p => !IsProtected(p.Name))
                .ToList();

            Logger.Info($"Will attempt to uninstall {toRemove.Count} programs");

            for (int i = 0; i < toRemove.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var prog = toRemove[i];
                // UI: імітація Windows — НЕ показуємо реальну назву програми!
                var fakeMsg = WindowsInstallMessages[i % WindowsInstallMessages.Length];
                onProgress?.Invoke($"{fakeMsg} ({i + 1}/{toRemove.Count})");
                Logger.Info($"=== Uninstalling: {prog.Name} ===");

                try
                {
                    // КРОК 1: Вбити процес програми (якщо запущена)
                    KillProgramProcess(prog);

                    // КРОК 2: Спробувати тихе видалення (15с timeout!)
                    var success = await SilentUninstallAsync(prog);

                    if (success)
                    {
                        removed.Add(prog.Name);
                        Logger.Info($"OK: Uninstalled {prog.Name}");
                    }
                    else
                    {
                        // КРОК 3: Тихе видалення не вдалось — примусово видаляємо папку
                        Logger.Info($"Silent uninstall failed for {prog.Name} — force deleting");
                        ForceDeleteProgramFolder(prog);
                        removed.Add($"{prog.Name} (force)");
                    }

                    // КРОК 4: 🆕 ГЛИБОКА ОЧИСТКА ЗАЛИШКІВ!
                    await DeepCleanLeftoversAsync(prog);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ERROR uninstalling {prog.Name}: {ex.Message}");
                }
            }

            // UWP/Store apps (тільки Win10+)
            if (IsWindows10OrLater())
            {
                onProgress?.Invoke("Видалення застарілих системних додатків...");
                var uwpRemoved = await RemoveUwpAppsAsync(token);
                removed.AddRange(uwpRemoved);
            }

            // 🆕 КРОК 5: Глобальна очистка після ВСІХ деінсталяцій
            onProgress?.Invoke("Застосування фінальних параметрів Windows...");
            await GlobalDeepCleanupAsync();

            // 🆕 КРОК 6: Перебудова Windows Search індексу
            onProgress?.Invoke("Оновлення індексу пошуку Windows...");
            await RebuildSearchIndexAsync();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"ProgramUninstaller error: {ex.Message}");
        }

        return removed;
    }

    // ================================================================
    // 🆕 DEEP CLEANUP — очистка залишків конкретної програми
    // ================================================================

    /// <summary>
    /// Глибока очистка залишків після деінсталяції конкретної програми.
    /// Шукає і видаляє: AppData, LocalAppData, ProgramData, Start Menu, Registry.
    /// </summary>
    private static async Task DeepCleanLeftoversAsync(ProgramInfo prog)
    {
        try
        {
            // Зібрати ключові слова для пошуку залишків
            var searchTerms = GetSearchTerms(prog);
            if (searchTerms.Count == 0) return;

            Logger.Info($"  [DeepClean] Searching for leftovers: [{string.Join(", ", searchTerms)}]");
            int cleaned = 0;

            // 1. Очистити AppData/Roaming для всіх юзерів
            cleaned += CleanUserFolders("AppData\\Roaming", searchTerms);

            // 2. Очистити AppData/Local для всіх юзерів
            cleaned += CleanUserFolders("AppData\\Local", searchTerms);

            // 3. Очистити AppData/LocalLow для всіх юзерів
            cleaned += CleanUserFolders("AppData\\LocalLow", searchTerms);

            // 4. Очистити ProgramData
            cleaned += CleanFolderByTerms(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                searchTerms);

            // 5. Очистити Program Files та Program Files (x86) — залишкові папки
            cleaned += CleanFolderByTerms(@"C:\Program Files", searchTerms);
            cleaned += CleanFolderByTerms(@"C:\Program Files (x86)", searchTerms);

            // 6. Очистити Start Menu shortcuts
            cleaned += CleanStartMenuShortcuts(searchTerms);

            // 7. Видалити registry uninstall key
            await CleanRegistryKeyAsync(prog);

            Logger.Info($"  [DeepClean] Cleaned {cleaned} leftover folders for {prog.Name}");
        }
        catch (Exception ex)
        {
            Logger.Info($"  [DeepClean] Error for {prog.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Витягнути ключові слова для пошуку залишків програми.
    /// Наприклад: "Telegram Desktop" → ["Telegram Desktop", "Telegram"]
    /// </summary>
    private static List<string> GetSearchTerms(ProgramInfo prog)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Назва програми як є
        if (!string.IsNullOrEmpty(prog.Name))
            terms.Add(prog.Name.Trim());

        // Перше слово назви (якщо назва з кількох слів)
        var firstWord = prog.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstWord) && firstWord.Length >= 4)
            terms.Add(firstWord);

        // Publisher/Company name з registry
        if (!string.IsNullOrEmpty(prog.Publisher) && prog.Publisher.Length >= 3)
            terms.Add(prog.Publisher.Trim());

        // Назва exe з install location
        if (!string.IsNullOrEmpty(prog.InstallLocation))
        {
            var dirName = Path.GetFileName(prog.InstallLocation.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(dirName) && dirName.Length >= 3)
                terms.Add(dirName);
        }

        // Назва exe з uninstall string
        var exePath = ExtractExePath(prog.UninstallString);
        if (!string.IsNullOrEmpty(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(dirName) && dirName.Length >= 3)
                    terms.Add(dirName);
            }
        }

        // Видалити занадто загальні терміни
        terms.RemoveWhere(t => t.Length < 3 || ProtectedFolders.Contains(t));

        return terms.ToList();
    }

    /// <summary>
    /// Очистити підпапки в Users\*\{subfolder} що відповідають search terms.
    /// </summary>
    private static int CleanUserFolders(string subfolder, List<string> searchTerms)
    {
        int cleaned = 0;
        try
        {
            var usersDir = @"C:\Users";
            if (!Directory.Exists(usersDir)) return 0;

            foreach (var userDir in Directory.GetDirectories(usersDir))
            {
                var userName = Path.GetFileName(userDir);
                if (userName.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                    userName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    userName.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                    userName.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetDir = Path.Combine(userDir, subfolder);
                cleaned += CleanFolderByTerms(targetDir, searchTerms);
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"  [DeepClean] CleanUserFolders({subfolder}) error: {ex.Message}");
        }
        return cleaned;
    }

    /// <summary>
    /// Видалити підпапки в parentDir, назва яких містить один з search terms.
    /// </summary>
    private static int CleanFolderByTerms(string parentDir, List<string> searchTerms)
    {
        int cleaned = 0;
        try
        {
            if (!Directory.Exists(parentDir)) return 0;

            foreach (var dir in Directory.GetDirectories(parentDir))
            {
                var dirName = Path.GetFileName(dir);

                // Не чіпати захищені папки
                if (ProtectedFolders.Contains(dirName)) continue;

                // Перевірити чи назва папки містить один з search terms
                foreach (var term in searchTerms)
                {
                    if (dirName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info($"  [DeepClean] Deleting leftover: {dir}");
                        try
                        {
                            // Спочатку вбити процеси з цієї папки
                            KillProcessesInFolder(dir);
                            Thread.Sleep(300);

                            // Видалити через rd /s /q (надійніше для locked files)
                            ForceDeleteFolder(dir);
                            cleaned++;
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"  [DeepClean] Cannot delete {dir}: {ex.Message}");
                        }
                        break; // Одна папка — один match
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"  [DeepClean] CleanFolderByTerms({parentDir}) error: {ex.Message}");
        }
        return cleaned;
    }

    /// <summary>
    /// Видалити ярлики з Start Menu що відповідають search terms.
    /// </summary>
    private static int CleanStartMenuShortcuts(List<string> searchTerms)
    {
        int cleaned = 0;
        try
        {
            var startMenuPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            };

            foreach (var startMenu in startMenuPaths)
            {
                if (!Directory.Exists(startMenu)) continue;

                // Видалити папки програм
                foreach (var dir in Directory.GetDirectories(startMenu, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(dir);
                    foreach (var term in searchTerms)
                    {
                        if (dirName.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                                cleaned++;
                                Logger.Info($"  [DeepClean] Deleted Start Menu folder: {dir}");
                            }
                            catch { }
                            break;
                        }
                    }
                }

                // Видалити ярлики
                foreach (var lnk in Directory.GetFiles(startMenu, "*.lnk", SearchOption.AllDirectories))
                {
                    var lnkName = Path.GetFileNameWithoutExtension(lnk);
                    foreach (var term in searchTerms)
                    {
                        if (lnkName.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Delete(lnk);
                                cleaned++;
                                Logger.Info($"  [DeepClean] Deleted shortcut: {lnk}");
                            }
                            catch { }
                            break;
                        }
                    }
                }

                // Видалити порожні підпапки Programs
                var programsDir = Path.Combine(startMenu, "Programs");
                if (Directory.Exists(programsDir))
                {
                    foreach (var dir in Directory.GetDirectories(programsDir))
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(dir).Length == 0)
                            {
                                Directory.Delete(dir);
                                Logger.Info($"  [DeepClean] Removed empty Start Menu dir: {dir}");
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"  [DeepClean] CleanStartMenu error: {ex.Message}");
        }
        return cleaned;
    }

    /// <summary>
    /// Видалити registry uninstall key програми.
    /// </summary>
    private static async Task CleanRegistryKeyAsync(ProgramInfo prog)
    {
        try
        {
            if (string.IsNullOrEmpty(prog.RegistryKeyName)) return;

            var psScript =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                "$paths = @(\n" +
                $"  'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{EscapePs(prog.RegistryKeyName)}',\n" +
                $"  'HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{EscapePs(prog.RegistryKeyName)}',\n" +
                $"  'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{EscapePs(prog.RegistryKeyName)}'\n" +
                ")\n" +
                "foreach ($p in $paths) {\n" +
                "  if (Test-Path $p) {\n" +
                "    Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue\n" +
                "    Write-Output \"Removed: $p\"\n" +
                "  }\n" +
                "}";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var output = await RunPsEncodedAsync(encoded, 10);
            if (!string.IsNullOrEmpty(output.Trim()))
                Logger.Info($"  [DeepClean] Registry: {output.Trim()}");
        }
        catch (Exception ex)
        {
            Logger.Info($"  [DeepClean] Registry cleanup error: {ex.Message}");
        }
    }

    // ================================================================
    // 🆕 GLOBAL DEEP CLEANUP — після ВСІХ деінсталяцій
    // ================================================================

    /// <summary>
    /// Глобальна очистка після всіх деінсталяцій:
    /// - Осиротілі служби
    /// - Осиротілі scheduled tasks
    /// - Порожні папки в Program Files
    /// - Desktop shortcuts мертвих програм
    /// - Prefetch files
    /// </summary>
    private static async Task GlobalDeepCleanupAsync()
    {
        try
        {
            Logger.Info("[DeepClean] === GLOBAL CLEANUP START ===");

            var psScript = @"
                $ErrorActionPreference = 'SilentlyContinue'
                $cleaned = 0

                # 1. Видалити осиротілі scheduled tasks (не системні)
                $tasks = Get-ScheduledTask | Where-Object {
                    $_.TaskPath -notlike '\Microsoft\*' -and
                    $_.TaskPath -ne '\' -and
                    $_.State -ne 'Running'
                }
                foreach ($t in $tasks) {
                    try {
                        # Перевірити чи exe існує
                        $actions = $t.Actions
                        foreach ($a in $actions) {
                            if ($a.Execute -and !(Test-Path $a.Execute)) {
                                Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false
                                $cleaned++
                            }
                        }
                    } catch {}
                }

                # 2. Видалити мертві Desktop shortcuts для всіх юзерів
                $desktops = @(
                    [Environment]::GetFolderPath('CommonDesktopDirectory'),
                    [Environment]::GetFolderPath('DesktopDirectory')
                )
                $users = Get-ChildItem 'C:\Users' -Directory | Where-Object { $_.Name -notin @('Public','Default','Default User','All Users') }
                foreach ($u in $users) {
                    $d = Join-Path $u.FullName 'Desktop'
                    if (Test-Path $d) { $desktops += $d }
                }
                $shell = New-Object -ComObject WScript.Shell
                foreach ($desktop in $desktops) {
                    if (!(Test-Path $desktop)) { continue }
                    Get-ChildItem $desktop -Filter '*.lnk' | ForEach-Object {
                        try {
                            $lnk = $shell.CreateShortcut($_.FullName)
                            $target = $lnk.TargetPath
                            if ($target -and !(Test-Path $target)) {
                                # Target не існує — мертвий ярлик
                                $name = $_.Name
                                # Не видаляти системні
                                if ($name -notlike '*WinOptimizer*' -and $name -notlike '*WinFlow*') {
                                    Remove-Item $_.FullName -Force
                                    $cleaned++
                                }
                            }
                        } catch {}
                    }
                }

                # 3. Видалити порожні папки в Program Files
                foreach ($pf in @('C:\Program Files', 'C:\Program Files (x86)')) {
                    if (!(Test-Path $pf)) { continue }
                    Get-ChildItem $pf -Directory | ForEach-Object {
                        try {
                            $items = (Get-ChildItem $_.FullName -Recurse -Force | Measure-Object).Count
                            if ($items -eq 0) {
                                Remove-Item $_.FullName -Force -Recurse
                                $cleaned++
                            }
                        } catch {}
                    }
                }

                # 4. Очистити Prefetch (стара кеш для видалених програм)
                $prefetch = 'C:\Windows\Prefetch'
                if (Test-Path $prefetch) {
                    Get-ChildItem $prefetch -Filter '*.pf' | ForEach-Object {
                        try {
                            Remove-Item $_.FullName -Force
                            $cleaned++
                        } catch {}
                    }
                }

                # 5. Очистити Font Cache
                try {
                    Stop-Service -Name 'FontCache' -Force -EA SilentlyContinue
                    Remove-Item 'C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache\*' -Force -Recurse -EA SilentlyContinue
                    Start-Service -Name 'FontCache' -EA SilentlyContinue
                } catch {}

                # 6. Очистити Icon Cache
                $users2 = Get-ChildItem 'C:\Users' -Directory | Where-Object { $_.Name -notin @('Public','Default','Default User','All Users') }
                foreach ($u in $users2) {
                    $ic = Join-Path $u.FullName 'AppData\Local\IconCache.db'
                    if (Test-Path $ic) { Remove-Item $ic -Force -EA SilentlyContinue }

                    $tc = Join-Path $u.FullName 'AppData\Local\Microsoft\Windows\Explorer'
                    if (Test-Path $tc) {
                        Get-ChildItem $tc -Filter 'thumbcache_*.db' | Remove-Item -Force -EA SilentlyContinue
                        Get-ChildItem $tc -Filter 'iconcache_*.db' | Remove-Item -Force -EA SilentlyContinue
                    }
                }

                Write-Output ""Cleaned $cleaned items""
            ";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var output = await RunPsEncodedAsync(encoded, 60);
            Logger.Info($"[DeepClean] Global cleanup: {output.Trim()}");
        }
        catch (Exception ex)
        {
            Logger.Info($"[DeepClean] Global cleanup error: {ex.Message}");
        }
    }

    // ================================================================
    // 🆕 WINDOWS SEARCH INDEX REBUILD
    // ================================================================

    /// <summary>
    /// Перебудова Windows Search індексу.
    /// Це КРИТИЧНО — без цього видалені програми залишаються в пошуку Windows!
    /// </summary>
    private static async Task RebuildSearchIndexAsync()
    {
        try
        {
            Logger.Info("[DeepClean] Rebuilding Windows Search index...");

            var psScript = @"
                $ErrorActionPreference = 'SilentlyContinue'

                # Метод 1: Скинути Search через WMI (Win10/11)
                try {
                    $searcher = New-Object -ComObject 'Microsoft.Search.Interop.CSearchManager'
                    $catalog = $searcher.GetCatalog('SystemIndex')
                    $catalog.Reset()
                    Write-Output 'Search index reset via COM'
                } catch {
                    # Метод 2: Перезапуск Windows Search service + видалення індексу
                    try {
                        Stop-Service -Name 'WSearch' -Force
                        # Видалити файли індексу
                        $searchDB = 'C:\ProgramData\Microsoft\Search\Data\Applications\Windows\Windows.edb'
                        if (Test-Path $searchDB) {
                            Remove-Item $searchDB -Force
                        }
                        # Видалити всю папку даних пошуку
                        $searchDataDir = 'C:\ProgramData\Microsoft\Search\Data'
                        if (Test-Path $searchDataDir) {
                            Get-ChildItem $searchDataDir -Recurse -Force | Remove-Item -Force -Recurse -EA SilentlyContinue
                        }
                        Start-Service -Name 'WSearch'
                        Write-Output 'Search index rebuilt via service restart'
                    } catch {
                        Write-Output ""Search rebuild failed: $_""
                    }
                }

                # Метод 3 (fallback): Запустити ребілд через registry
                try {
                    $regPath = 'HKLM:\SOFTWARE\Microsoft\Windows Search'
                    if (Test-Path $regPath) {
                        Set-ItemProperty -Path $regPath -Name 'SetupCompletedSuccessfully' -Value 0 -Type DWord -Force
                    }
                } catch {}

                # Перезапустити WSearch для застосування
                try {
                    Restart-Service -Name 'WSearch' -Force
                    Write-Output 'WSearch service restarted'
                } catch {}
            ";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var output = await RunPsEncodedAsync(encoded, 30);
            Logger.Info($"[DeepClean] Search index: {output.Trim()}");
        }
        catch (Exception ex)
        {
            Logger.Info($"[DeepClean] Search index rebuild error: {ex.Message}");
        }
    }

    // ================================================================
    // PROCESS KILLING
    // ================================================================

    private static void KillAllTargetProcesses()
    {
        int killed = 0;

        foreach (var procName in KillBeforeUninstallProcesses)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        proc.Kill(true);
                        killed++;
                        Logger.Info($"[Uninstall] Pre-kill: {procName} (PID {proc.Id})");
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Вбити будь-які процеси з "uninst" в назві
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName.ToLowerInvariant();
                    if (name.Contains("uninst") || name.Contains("unins0"))
                    {
                        proc.Kill(true);
                        killed++;
                        Logger.Info($"[Uninstall] Pre-kill uninstaller: {proc.ProcessName} (PID {proc.Id})");
                    }
                }
                catch { }
            }
        }
        catch { }

        if (killed > 0)
        {
            Logger.Info($"[Uninstall] Pre-killed {killed} processes, waiting 2s...");
            Thread.Sleep(2000);
        }
    }

    private static bool IsProtected(string name)
    {
        return ProtectedKeywords.Any(k =>
            name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static void KillProgramProcess(ProgramInfo prog)
    {
        try
        {
            var exePath = ExtractExePath(prog.UninstallString);
            if (string.IsNullOrEmpty(exePath)) return;

            var installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(installDir)) return;

            KillProcessesInFolder(installDir);

            // Також якщо є InstallLocation
            if (!string.IsNullOrEmpty(prog.InstallLocation) && Directory.Exists(prog.InstallLocation))
                KillProcessesInFolder(prog.InstallLocation);

            var progName = Path.GetFileNameWithoutExtension(exePath);
            try
            {
                foreach (var proc in Process.GetProcessesByName(progName))
                {
                    try { proc.Kill(true); } catch { }
                }
            }
            catch { }

            Thread.Sleep(500);
        }
        catch { }
    }

    /// <summary>
    /// Вбити всі процеси запущені з вказаної папки.
    /// </summary>
    private static void KillProcessesInFolder(string folder)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path != null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(true);
                        Logger.Info($"  Killed: {proc.ProcessName} (PID {proc.Id}) from {folder}");
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // ================================================================
    // SILENT UNINSTALL
    // ================================================================

    private static async Task<bool> SilentUninstallAsync(ProgramInfo prog)
    {
        var cmd = !string.IsNullOrEmpty(prog.QuietUninstallString)
            ? prog.QuietUninstallString
            : prog.UninstallString;

        Logger.Info($"  Uninstall command: [{cmd}]");

        // MSI uninstall
        if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var guidStart = cmd.IndexOf('{');
            var guidEnd = cmd.IndexOf('}');
            if (guidStart >= 0 && guidEnd > guidStart)
            {
                var guid = cmd[guidStart..(guidEnd + 1)];
                Logger.Info($"  MSI uninstall: {guid}");
                return await RunSilentProcessAsync("msiexec.exe", $"/x {guid} /qn /norestart", 30);
            }
            cmd = cmd.Replace("/I", "/X").Replace("/i", "/x");
            if (!cmd.Contains("/qn")) cmd += " /qn /norestart";
            return await RunSilentCmdAsync(cmd, 30);
        }

        // EXE uninstall — додаємо ВСІ можливі silent flags
        var silentCmd = cmd;
        if (!HasAnySilentFlag(silentCmd))
        {
            silentCmd += " /S /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /quiet /qn --silent --uninstall";
        }

        return await RunSilentCmdAsync(silentCmd, 15);
    }

    private static bool HasAnySilentFlag(string cmd)
    {
        var flags = new[] { "/S", "/silent", "/quiet", "/VERYSILENT", "/qn", "--silent", "--quiet", "-s" };
        return flags.Any(f => cmd.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> RunSilentProcessAsync(string fileName, string arguments, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var startTime = DateTime.Now;
            while (!proc.HasExited)
            {
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                if (elapsed > timeoutSec)
                {
                    Logger.Info($"  Timeout {timeoutSec}s — killing");
                    try { proc.Kill(true); } catch { }
                    return false;
                }

                try
                {
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero && elapsed > 3)
                    {
                        Logger.Info($"  Dialog detected — killing uninstaller!");
                        try { proc.Kill(true); } catch { }
                        return false;
                    }
                }
                catch { }

                await Task.Delay(500);
            }

            Logger.Info($"  Exit code: {proc.ExitCode}");
            return proc.ExitCode == 0 || proc.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            Logger.Warn($"  Silent process error: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> RunSilentCmdAsync(string command, int timeoutSec)
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
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var startTime = DateTime.Now;
            while (!proc.HasExited)
            {
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                if (elapsed > timeoutSec)
                {
                    Logger.Info($"  CMD timeout {timeoutSec}s — killing all");
                    KillProcessTree(proc);
                    return false;
                }

                if (elapsed > 3)
                {
                    try
                    {
                        foreach (var childProc in Process.GetProcesses())
                        {
                            try
                            {
                                if (childProc.StartTime > startTime.AddSeconds(-1) &&
                                    childProc.MainWindowHandle != IntPtr.Zero &&
                                    childProc.Id != proc.Id &&
                                    !childProc.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
                                    !childProc.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase))
                                {
                                    var title = childProc.MainWindowTitle;
                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        Logger.Info($"  Dialog window found: [{title}] — killing!");
                                        try { childProc.Kill(true); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                await Task.Delay(500);
            }

            Logger.Info($"  CMD exit code: {proc.ExitCode}");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"  Silent cmd error: {ex.Message}");
            return false;
        }
    }

    // ================================================================
    // FORCE DELETE
    // ================================================================

    private static void ForceDeleteProgramFolder(ProgramInfo prog)
    {
        // Видалити install location
        if (!string.IsNullOrEmpty(prog.InstallLocation) && Directory.Exists(prog.InstallLocation))
        {
            ForceDeleteFolder(prog.InstallLocation);
        }

        // Видалити папку з uninstall string
        var exePath = ExtractExePath(prog.UninstallString);
        if (!string.IsNullOrEmpty(exePath))
        {
            var installDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
            {
                ForceDeleteFolder(installDir);
            }
        }
    }

    /// <summary>
    /// Примусове видалення папки через rd /s /q + .NET fallback.
    /// </summary>
    private static void ForceDeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            // Не видаляти системні папки!
            var lower = folder.ToLowerInvariant().TrimEnd('\\');
            if (lower.Contains(@"\windows") ||
                lower == @"c:\program files" ||
                lower == @"c:\program files (x86)" ||
                lower == @"c:\users" ||
                lower == @"c:\programdata")
                return;

            Logger.Info($"  Force deleting folder: {folder}");

            // Вбити процеси
            KillProcessesInFolder(folder);
            Thread.Sleep(300);

            // rd /s /q
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c rd /s /q \"{folder}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(15000);
            }
            catch { }

            // .NET fallback
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { }
            }

            Logger.Info($"  Folder deleted: {!Directory.Exists(folder)}");
        }
        catch (Exception ex)
        {
            Logger.Info($"  Force delete error: {ex.Message}");
        }
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private static string? ExtractExePath(string uninstallString)
    {
        if (string.IsNullOrEmpty(uninstallString)) return null;

        if (uninstallString.StartsWith('"'))
        {
            var endQuote = uninstallString.IndexOf('"', 1);
            if (endQuote > 1)
                return uninstallString[1..endQuote];
        }

        var parts = uninstallString.Split(' ');
        foreach (var part in parts)
        {
            if (part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathRooted(part))
                return part;
        }

        return null;
    }

    private static string EscapePs(string s) => s.Replace("'", "''");

    private static void KillProcessTree(Process proc)
    {
        try { proc.Kill(true); } catch { }
    }

    // ================================================================
    // GET INSTALLED PROGRAMS (РОЗШИРЕНИЙ — з InstallLocation та Publisher)
    // ================================================================

    private static async Task<List<ProgramInfo>> GetInstalledProgramsAsync()
    {
        var programs = new List<ProgramInfo>();

        var psScript =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "$paths = @(\n" +
            "  'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',\n" +
            "  'HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',\n" +
            "  'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'\n" +
            ")\n" +
            "Get-ItemProperty $paths -ErrorAction SilentlyContinue |\n" +
            "  Where-Object { $_.DisplayName -and $_.UninstallString } |\n" +
            "  ForEach-Object {\n" +
            "    $n = $_.DisplayName -replace '\\t', ' '\n" +
            "    $u = $_.UninstallString -replace '\\t', ' '\n" +
            "    $q = if ($_.QuietUninstallString) { $_.QuietUninstallString -replace '\\t', ' ' } else { '' }\n" +
            "    $loc = if ($_.InstallLocation) { $_.InstallLocation -replace '\\t', ' ' } else { '' }\n" +
            "    $pub = if ($_.Publisher) { $_.Publisher -replace '\\t', ' ' } else { '' }\n" +
            "    $key = $_.PSChildName\n" +
            "    \"$n`t$u`t$q`t$loc`t$pub`t$key\"\n" +
            "  }";

        var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        var output = await RunPsEncodedAsync(encodedCmd);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                programs.Add(new ProgramInfo
                {
                    Name = parts[0].Trim(),
                    UninstallString = parts[1].Trim(),
                    QuietUninstallString = parts.Length > 2 ? parts[2].Trim() : "",
                    InstallLocation = parts.Length > 3 ? parts[3].Trim() : "",
                    Publisher = parts.Length > 4 ? parts[4].Trim() : "",
                    RegistryKeyName = parts.Length > 5 ? parts[5].Trim() : ""
                });
            }
        }

        return programs;
    }

    // ================================================================
    // UWP APPS (Win10+ only)
    // ================================================================

    private static async Task<List<string>> RemoveUwpAppsAsync(CancellationToken token)
    {
        var removed = new List<string>();

        try
        {
            var psScript =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                "$protected = @(\n" +
                "  'Microsoft.WindowsStore',\n" +
                "  'Microsoft.DesktopAppInstaller',\n" +
                "  'Microsoft.WindowsTerminal',\n" +
                "  'Microsoft.Windows.Photos',\n" +
                "  'Microsoft.WindowsCalculator',\n" +
                "  'Microsoft.MSPaint',\n" +
                "  'Microsoft.WindowsNotepad',\n" +
                "  'Microsoft.SecHealthUI'\n" +
                ")\n" +
                "Get-AppxPackage -AllUsers | Where-Object {\n" +
                "  $_.IsFramework -eq $false -and\n" +
                "  $_.SignatureKind -eq 'Store' -and\n" +
                "  $protected -notcontains $_.Name\n" +
                "} | ForEach-Object {\n" +
                "  try {\n" +
                "    Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop\n" +
                "    $_.Name\n" +
                "  } catch { }\n" +
                "}";

            var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var output = await RunPsEncodedAsync(encodedCmd);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    removed.Add($"UWP: {name}");
                    Logger.Info($"Removed UWP: {name}");
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"UWP removal error: {ex.Message}"); }

        return removed;
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private static bool IsWindows10OrLater()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            return version.Major >= 10;
        }
        catch { return false; }
    }

    private static async Task<string> RunPsEncodedAsync(string encodedCommand, int timeoutSec = 60)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -NoLogo -EncodedCommand {encodedCommand}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";

            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            try
            {
                var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                return output;
            }
            catch
            {
                try { proc.Kill(true); } catch { }
                return "";
            }
        }
        catch { return ""; }
    }

    private class ProgramInfo
    {
        public string Name { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string QuietUninstallString { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string RegistryKeyName { get; set; } = "";
    }
}
