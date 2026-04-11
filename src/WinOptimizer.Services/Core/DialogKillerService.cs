using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Core;

/// <summary>
/// ГЛОБАЛЬНИЙ УБИВЦЯ ДІАЛОГІВ v2.0
/// Працює у фоні протягом ВСІЄЇ оптимізації.
/// Кожні 1.5 секунди сканує всі вікна і вбиває:
/// - Діалоги видалення (BlueStacks, TLauncher, будь-які uninstall wizards)
/// - Системні діалоги помилок (MSVCR100.dll, інші missing DLL)
/// - "Як ви хочете відкрити?" (OpenWith)
/// - Будь-які UAC/confirmation діалоги
///
/// v2.0: EnumWindows API для пошуку ВСІХ вікон (не тільки MainWindow)
///       + вбивство по назві процесу (uninstaller-и)
/// </summary>
public static class DialogKillerService
{
    // === Windows API ===
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    // Processes to ALWAYS kill on sight (exact name match)
    private static readonly string[] KillOnSightProcesses =
    {
        "OpenWith",          // "Як ви хочете відкрити?"
        "WerFault",          // Windows Error Reporting
        "dwwin",             // Dr. Watson
        "dw20",              // Dr. Watson
        "splwow64",          // Print spooler dialog
    };

    // Process NAME PATTERNS — if process name CONTAINS any of these, KILL
    private static readonly string[] KillProcessNamePatterns =
    {
        "uninst",            // uninstall, unins000, uninst, BlueStacksUninstaller
        "unins0",            // Inno Setup: unins000.exe
        "bsuninstall",       // BlueStacks uninstaller
        "hd-uninstall",      // BlueStacks HD uninstaller
        "bluestacksuninstall", // BlueStacks
        "au_",               // AutoUpdate / auto-uninstall
    };

    // Window title keywords — if ANY window contains these, KILL the process
    private static readonly string[] KillTitleKeywords =
    {
        // Uninstall surveys/wizards
        "uninstall", "удалить", "удаление", "деінсталяція", "видалення",
        "почему вы решили", "why are you uninstalling",
        "пожалуйста, сообщите", "please tell us",
        "feedback", "survey", "опрос", "опитування",
        "setup wizard", "мастер установки", "мастер удаления",
        "bluestacks", // BlueStacks будь-яке вікно під час видалення
        "before you go", "перед тим як піти",
        "help us improve", "допоможіть покращити",
        "rate us", "оцініть нас",
        "what went wrong", "що пішло не так",
        "reason for", "причина",
        "tell us why", "розкажіть чому",
        "share your experience", "поділіться",
        "would you recommend", "порекомендували б",

        // Error dialogs
        "системная ошибка", "system error",
        "не удается продолжить", "cannot continue",
        "не удается открыть", "не вдається відкрити",
        "cannot open application",
        "msvcr", "msvcp", "vcruntime", "api-ms-win",
        "missing dll", "отсутствует dll",
        ".dll", // Any DLL error dialog
        "см. в store", "see in store", "магазине windows",
        "error", "помилка", "ошибка",

        // Open With dialog
        "как вы хотите открыть", "how do you want to open",
        "як ви хочете відкрити",

        // Restart/reboot prompts
        "restart now", "перезагрузить сейчас", "restart later",
        "reboot required", "потрібен перезапуск",
        "restart your computer", "перезавантажити",

        // Generic confirmation dialogs from uninstallers
        "are you sure", "вы уверены", "ви впевнені",
        "подтвердите", "confirm",
        "do you want to", "хотите ли",
        "would you like to", "бажаєте",
        "save changes", "зберегти зміни",
        "close program", "закрити програму",

        // Update/upgrade prompts
        "update available", "оновлення доступне",
        "new version", "нова версія",
        "upgrade now", "оновити зараз",
    };

    // Window CLASS names that are typically dialogs/popups
    private static readonly string[] KillWindowClasses =
    {
        "#32770",  // Standard Windows dialog class
    };

    // Window title keywords to IGNORE (don't kill these)
    private static readonly string[] IgnoreTitleKeywords =
    {
        "winflow", "winoptimizer", "installer", // Our own app!
        "task manager", "диспетчер",
        "explorer", "провідник", "проводник",
        "встановлення windows", "переустановка", "переустановку", // Наші екрани!
    };

    // Process names to IGNORE (never kill)
    private static readonly string[] IgnoreProcessNames =
    {
        "winoptimizer", "winflow", "explorer", "svchost",
        "csrss", "lsass", "services", "System",
        "taskmgr", "dwm", "sihost", "ctfmon",
    };

    private static CancellationTokenSource? _cts;
    private static Task? _killerTask;

    // Track killed PIDs to avoid spam logging
    private static readonly HashSet<int> _killedPids = new();

    /// <summary>
    /// Запустити глобальний dialog killer.
    /// Повинен викликатись НА ПОЧАТКУ оптимізації.
    /// </summary>
    public static void Start()
    {
        _killedPids.Clear();

        // 1. Suppress system error dialogs (missing DLL, etc.)
        try
        {
            var prevMode = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
            Logger.Info($"[DialogKiller] SetErrorMode applied (prev: {prevMode})");
        }
        catch (Exception ex)
        {
            Logger.Info($"[DialogKiller] SetErrorMode failed: {ex.Message}");
        }

        // 2. Start background killer task
        _cts = new CancellationTokenSource();
        _killerTask = Task.Run(() => KillerLoopAsync(_cts.Token));
        Logger.Info("[DialogKiller] v2.0 Started (EnumWindows + process patterns)");
    }

    /// <summary>
    /// Зупинити dialog killer (по завершенню оптимізації).
    /// </summary>
    public static void Stop()
    {
        try
        {
            _cts?.Cancel();
            _killerTask?.Wait(3000);
        }
        catch { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _killerTask = null;
        }
        _killedPids.Clear();
        Logger.Info("[DialogKiller] Stopped");
    }

    /// <summary>
    /// Головний цикл — кожні 1.5с сканує всі вікна.
    /// </summary>
    private static async Task KillerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct); // 500мс — швидко вбивати діалоги!
                ScanAndKillDialogs();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Info($"[DialogKiller] Scan error: {ex.Message}");
            }
        }
    }

    private static void ScanAndKillDialogs()
    {
        // 1. Kill known problematic processes by exact name
        foreach (var procName in KillOnSightProcesses)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        if (!_killedPids.Contains(proc.Id))
                        {
                            proc.Kill();
                            _killedPids.Add(proc.Id);
                            Logger.Info($"[DialogKiller] Killed process: {procName} (PID {proc.Id})");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 2. Kill processes by name PATTERN (uninst*, bluestacksuninstall*, etc.)
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4 || _killedPids.Contains(proc.Id)) continue;

                    var procNameLower = proc.ProcessName.ToLowerInvariant();

                    // Skip safe processes
                    if (IgnoreProcessNames.Any(ip => procNameLower.Equals(ip, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Check if process name matches uninstaller patterns
                    if (KillProcessNamePatterns.Any(p => procNameLower.Contains(p.ToLowerInvariant())))
                    {
                        Logger.Info($"[DialogKiller] Killing uninstaller process: {proc.ProcessName} (PID {proc.Id})");
                        try { proc.Kill(); } catch { }
                        _killedPids.Add(proc.Id);
                    }
                }
                catch { }
            }
        }
        catch { }

        // 3. EnumWindows — scan ALL visible windows (not just MainWindowHandle)
        try
        {
            var pidsToKill = new HashSet<uint>();

            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    // Get window title
                    var titleLen = GetWindowTextLength(hWnd);
                    if (titleLen == 0) return true;

                    var sb = new StringBuilder(titleLen + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    var title = sb.ToString();
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    var titleLower = title.ToLowerInvariant();

                    // Skip our app and safe windows
                    if (IgnoreTitleKeywords.Any(k => titleLower.Contains(k.ToLowerInvariant())))
                        return true;

                    // Check if title matches any kill keyword
                    if (KillTitleKeywords.Any(k => titleLower.Contains(k.ToLowerInvariant())))
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (pid > 4)
                            pidsToKill.Add(pid);
                    }
                    else
                    {
                        // Also check window class for standard dialogs (#32770)
                        // that appeared from uninstaller processes
                        var classSb = new StringBuilder(256);
                        GetClassName(hWnd, classSb, classSb.Capacity);
                        var className = classSb.ToString();

                        if (KillWindowClasses.Any(c => c.Equals(className, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Standard dialog — check if it belongs to an uninstaller process
                            GetWindowThreadProcessId(hWnd, out uint pid);
                            if (pid > 4)
                            {
                                try
                                {
                                    var dialogProc = Process.GetProcessById((int)pid);
                                    var dialogProcName = dialogProc.ProcessName.ToLowerInvariant();

                                    // Kill if it's from an uninstaller
                                    if (KillProcessNamePatterns.Any(p => dialogProcName.Contains(p.ToLowerInvariant())))
                                    {
                                        pidsToKill.Add(pid);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
                return true; // Continue enumeration
            }, IntPtr.Zero);

            // Kill collected processes
            foreach (var pid in pidsToKill)
            {
                if (_killedPids.Contains((int)pid)) continue;
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    var procName = proc.ProcessName;

                    // Final safety check
                    if (IgnoreProcessNames.Any(ip => procName.Equals(ip, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string title = "";
                    try { title = proc.MainWindowTitle; } catch { }

                    Logger.Info($"[DialogKiller] EnumWindows kill: [{title}] process: {procName} (PID {pid})");
                    proc.Kill();
                    _killedPids.Add((int)pid);
                }
                catch { }
            }
        }
        catch { }

        // 4. Scan processes with MainWindowHandle (original method as backup)
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4 || _killedPids.Contains(proc.Id)) continue;

                    string title;
                    try
                    {
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;
                        title = proc.MainWindowTitle;
                        if (string.IsNullOrEmpty(title)) continue;
                    }
                    catch { continue; }

                    var titleLower = title.ToLowerInvariant();

                    // Skip safe processes
                    if (IgnoreTitleKeywords.Any(k => titleLower.Contains(k.ToLowerInvariant())))
                        continue;

                    // Check title keywords
                    if (KillTitleKeywords.Any(k => titleLower.Contains(k.ToLowerInvariant())))
                    {
                        Logger.Info($"[DialogKiller] MainWindow kill: [{title}] (process: {proc.ProcessName}, PID: {proc.Id})");
                        try { proc.Kill(); } catch { }
                        _killedPids.Add(proc.Id);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
