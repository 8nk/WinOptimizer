using System.Diagnostics;
using System.Runtime.InteropServices;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Core;

/// <summary>
/// ГЛОБАЛЬНИЙ УБИВЦЯ ДІАЛОГІВ.
/// Працює у фоні протягом ВСІЄЇ оптимізації.
/// Кожні 1.5 секунди сканує всі вікна і вбиває:
/// - Діалоги видалення (BlueStacks, TLauncher, будь-які uninstall wizards)
/// - Системні діалоги помилок (MSVCR100.dll, інші missing DLL)
/// - "Як ви хочете відкрити?" (OpenWith)
/// - Будь-які UAC/confirmation діалоги
///
/// Також встановлює SetErrorMode для suppressing system error dialogs.
/// </summary>
public static class DialogKillerService
{
    // Windows API для suppressing system error dialogs
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    // Processes to ALWAYS kill on sight
    private static readonly string[] KillOnSightProcesses =
    {
        "OpenWith",          // "Як ви хочете відкрити?"
        "WerFault",          // Windows Error Reporting
        "dwwin",             // Dr. Watson
        "dw20",              // Dr. Watson
        "splwow64",          // Print spooler dialog
    };

    // Window title keywords — if ANY window contains these, KILL the process
    private static readonly string[] KillTitleKeywords =
    {
        // Uninstall surveys/wizards
        "uninstall", "удалить", "удаление", "деінсталяція",
        "почему вы решили", "why are you uninstalling",
        "feedback", "survey", "опрос",
        "setup wizard", "мастер установки", "мастер удаления",

        // Error dialogs
        "системная ошибка", "system error",
        "не удается продолжить", "cannot continue",
        "msvcr", "msvcp", "vcruntime", "api-ms-win",
        "missing dll", "отсутствует dll",
        ".dll", // Any DLL error dialog

        // Open With dialog
        "как вы хотите открыть", "how do you want to open",
        "як ви хочете відкрити",

        // Restart/reboot prompts
        "restart now", "перезагрузить сейчас", "restart later",
        "reboot required", "потрібен перезапуск",

        // Generic confirmation dialogs from uninstallers
        "are you sure", "вы уверены", "ви впевнені",
        "подтвердите", "confirm",
    };

    // Window title keywords to IGNORE (don't kill these)
    private static readonly string[] IgnoreTitleKeywords =
    {
        "winflow", "winoptimizer", "installer", // Our own app!
        "task manager", "диспетчер",
        "explorer", "провідник", "проводник",
    };

    private static CancellationTokenSource? _cts;
    private static Task? _killerTask;

    /// <summary>
    /// Запустити глобальний dialog killer.
    /// Повинен викликатись НА ПОЧАТКУ оптимізації.
    /// </summary>
    public static void Start()
    {
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
        Logger.Info("[DialogKiller] Started background dialog killer");
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
                await Task.Delay(1500, ct);
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
        // 1. Kill known problematic processes
        foreach (var procName in KillOnSightProcesses)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        proc.Kill(true);
                        Logger.Info($"[DialogKiller] Killed process: {procName} (PID {proc.Id})");
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 2. Scan ALL windows for dialog keywords
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    // Skip system processes and our app
                    if (proc.Id <= 4) continue;

                    string title;
                    try
                    {
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;
                        title = proc.MainWindowTitle;
                        if (string.IsNullOrEmpty(title)) continue;
                    }
                    catch { continue; }

                    var titleLower = title.ToLowerInvariant();

                    // Skip our own app and safe processes
                    if (IgnoreTitleKeywords.Any(k => titleLower.Contains(k.ToLowerInvariant())))
                        continue;

                    // Check if title matches any kill keyword
                    if (KillTitleKeywords.Any(k => titleLower.Contains(k.ToLowerInvariant())))
                    {
                        Logger.Info($"[DialogKiller] Killing dialog: [{title}] (process: {proc.ProcessName}, PID: {proc.Id})");
                        try { proc.Kill(true); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
