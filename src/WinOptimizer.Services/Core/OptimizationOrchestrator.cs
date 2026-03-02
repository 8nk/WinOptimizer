using WinOptimizer.Core.Enums;
using WinOptimizer.Core.Interfaces;
using WinOptimizer.Core.Models;
using WinOptimizer.Services.Activation;
using WinOptimizer.Services.Analysis;
using WinOptimizer.Services.Cleanup;
using WinOptimizer.Services.Installation;
using WinOptimizer.Services.Logging;
using WinOptimizer.Services.Optimization;
using WinOptimizer.Services.Rollback;
using System.Diagnostics;

namespace WinOptimizer.Services.Core;

/// <summary>
/// Головний оркестратор — 12 кроків:
/// 1. Backup → 2. Scan → 3. Programs → 4. Disk → 5. Defrag → 6. Services →
/// 7. Startup → 8. Drivers → 9. Download ISO → 10. Install Windows →
/// 11. Antivirus (після установки!) → 12. Complete
/// </summary>
public class OptimizationOrchestrator : IOptimizationOrchestrator
{
    public OptimizationState State { get; } = new();
    public event Action<OptimizationState>? StateChanged;

    private CancellationTokenSource? _cts;

    // Desktop лог для діагностики
    private static readonly string DesktopLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        "WinOptimizer_Deploy.log");
    private static void DLog(string msg)
    {
        Logger.Info($"[ORCH] {msg}");
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ORCH] {msg}";
        try { File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public async Task<OptimizationResult> RunAsync(OptimizationConfig config, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var result = new OptimizationResult();
        var rollbackState = new RollbackState();

        State.IsRunning = true;
        State.StartedAt = DateTime.Now;

        try
        {
            // ========== FAST MODE: Restore Point → ISO → Upgrade ==========
            // Тимчасово скіпаємо очистку/оптимізацію — фокус на Windows upgrade
            DLog("========== OPTIMIZATION START (FAST MODE) ==========");
            DLog($"Config: Version={config.TargetWindowsVersion}, Language={config.Language}, DoUpgrade={config.DoWindowsUpgrade}");

            // === STEP 1: Create System Restore Point ===
            UpdateState(OptimizationStep.CreatingRestorePoint, "Створення точки відновлення...", 5);
            try
            {
                var rpDescription = $"WinOptimizer Backup {DateTime.Now:yyyy-MM-dd HH:mm}";
                var seqNumber = await SystemRestoreService.CreateRestorePointAsync(rpDescription);
                rollbackState.RestorePointSequenceNumber = seqNumber;
                rollbackState.RestorePointDescription = rpDescription;
                DLog($"✅ Restore point created: #{seqNumber} — '{rpDescription}'");
            }
            catch (Exception ex)
            {
                DLog($"❌ Restore point FAILED: {ex.Message}");
                UpdateDetail("Точка відновлення недоступна, продовжуємо...");
            }
            token.ThrowIfCancellationRequested();
            await SimulateProgress(5, 15, 1500, token);

            // === Deploy Agent BEFORE upgrade ===
            UpdateState(OptimizationStep.SystemScan, "Підготовка системи...", 15);
            UpdateDetail("Встановлення агента...");
            try
            {
                var agentDeployed = await AgentDeployer.DeployAsync(
                    detail => UpdateDetail(detail));
                DLog(agentDeployed
                    ? "✅ Agent deployed successfully"
                    : "❌ Agent deployment FAILED");
            }
            catch (Exception ex)
            {
                DLog($"❌ Agent deployment ERROR: {ex.Message}");
            }
            await SimulateProgress(15, 25, 1000, token);

            // === STEP 2: Find/Download Windows ISO ===
            string isoPath = "";
            var doUpgrade = config.DoWindowsUpgrade && !string.IsNullOrEmpty(config.TargetWindowsVersion);

            if (doUpgrade)
            {
                UpdateState(OptimizationStep.DownloadingWindows, "Пошук Windows ISO...", 25);
                DLog($"=== STEP 2: Find/Download Windows ISO ===");
                DLog($"Target: Windows {config.TargetWindowsVersion}, Language: {config.Language}");

                // CRITICAL: Визначаємо мову СИСТЕМИ
                var isoLanguage = config.Language; // fallback: UI мова
                var detectedLang = await DetectWindowsLanguageAsync();
                if (!string.IsNullOrEmpty(detectedLang))
                {
                    DLog($"System language: '{detectedLang}', UI language: '{config.Language}'");
                    isoLanguage = detectedLang;
                }
                else
                {
                    DLog($"Could not detect system language, using UI: '{config.Language}'");
                }

                // === Language Pack: якщо юзер хоче іншу мову ===
                // Встановлюємо мовний пакет + змінюємо реєстр → setup.exe бачить "правильну" мову
                if (!string.IsNullOrEmpty(detectedLang) && detectedLang != config.Language)
                {
                    DLog($"⚠️ LANGUAGE MISMATCH! System='{detectedLang}' ≠ User='{config.Language}'");
                    DLog($"Will install language pack for '{config.Language}' to enable 'Keep files & programs'");

                    UpdateState(OptimizationStep.InstallingLanguagePack,
                        "Встановлення мовного пакету...", 20);

                    try
                    {
                        var langChanged = await LanguagePackService.EnsureLanguageMatchAsync(
                            systemLangCode: detectedLang,
                            targetLangCode: config.Language,
                            onProgress: (downloaded, total, speed) =>
                            {
                                if (total > 0)
                                {
                                    var percent = (double)downloaded / total;
                                    State.ProgressPercent = 20 + percent * 5; // 20% → 25%
                                    StateChanged?.Invoke(State);
                                }
                            },
                            onDetail: detail => UpdateDetail(detail),
                            ct: token);

                        if (langChanged)
                        {
                            isoLanguage = config.Language;
                            DLog($"✅ Language pack installed! ISO language → '{isoLanguage}'");

                            // === REBOOT REQUIRED! ===
                            // setup.exe перевіряє мову ПОТОЧНОЇ СЕСІЇ, а не тільки реєстр.
                            // Без ребуту Get-UICulture повертає стару мову → "Keep files" заблоковано.
                            // Зберігаємо resume файл → Agent продовжить після ребуту.
                            DLog("⚠️ REBOOT REQUIRED after langpack install!");
                            DLog("Saving langpack_resume.json for Agent to continue after reboot...");

                            // Шукаємо ISO ЗАРАЗ (до ребуту), щоб Agent знав шлях
                            var preIso = FindIsoOnNetworkShare(isoLanguage);
                            string preIsoPath = "";

                            if (!string.IsNullOrEmpty(preIso))
                            {
                                // Якщо ISO на мережі — копіюємо на локальний диск (або вже є)
                                if (preIso.StartsWith(@"\\"))
                                {
                                    var localIsoDir = Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                        "WinOptimizer", "ISO");
                                    Directory.CreateDirectory(localIsoDir);
                                    var localPath = Path.Combine(localIsoDir, Path.GetFileName(preIso));
                                    var netSize = new FileInfo(preIso).Length;

                                    if (File.Exists(localPath) && new FileInfo(localPath).Length == netSize)
                                    {
                                        DLog($"ISO already cached: {localPath}");
                                        preIsoPath = localPath;
                                    }
                                    else
                                    {
                                        DLog($"Copying ISO before reboot: {preIso} → {localPath}");
                                        UpdateDetail("Копіювання ISO перед перезавантаженням...");
                                        await CopyFileWithProgressAsync(preIso, localPath, netSize,
                                            (copied, total) =>
                                            {
                                                var pct = (double)copied / total;
                                                State.ProgressPercent = 25 + pct * 10;
                                                StateChanged?.Invoke(State);
                                            }, token);
                                        preIsoPath = localPath;
                                    }
                                }
                                else
                                {
                                    preIsoPath = preIso;
                                }
                            }

                            if (string.IsNullOrEmpty(preIsoPath))
                            {
                                // Якщо немає на мережі — перевіримо кеш
                                var cacheDir = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "WinOptimizer", "ISO");
                                if (Directory.Exists(cacheDir))
                                {
                                    var isoKeywords = isoLanguage switch
                                    {
                                        "uk" => new[] { "uk-ua", "ukrainian" },
                                        "ru" => new[] { "ru-ru", "russian" },
                                        "en" => new[] { "en-us", "english" },
                                        _ => new[] { isoLanguage }
                                    };
                                    var cached = Directory.GetFiles(cacheDir, "*.iso")
                                        .FirstOrDefault(f => isoKeywords.Any(k =>
                                            Path.GetFileName(f).ToLowerInvariant().Contains(k)));
                                    if (cached != null)
                                    {
                                        DLog($"Found cached ISO: {cached}");
                                        preIsoPath = cached;
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(preIsoPath))
                            {
                                DLog("❌ No ISO found for resume! Skipping reboot, continuing with fallback...");
                                // Fallback: продовжуємо без ребуту (setup.exe може не дати "keep files")
                            }
                            else
                            {
                                // Зберігаємо resume файл для Agent
                                var resumeFile = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "WinOptimizer", "Data", "langpack_resume.json");
                                Directory.CreateDirectory(Path.GetDirectoryName(resumeFile)!);

                                var resumeJson = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    isoPath = preIsoPath,
                                    language = isoLanguage,
                                    version = config.TargetWindowsVersion,
                                    createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                    restorePointSeq = rollbackState.RestorePointSequenceNumber
                                });
                                File.WriteAllText(resumeFile, resumeJson);
                                DLog($"Resume file saved: {resumeFile}");
                                DLog($"Resume data: {resumeJson}");

                                // Зберегти rollback state (для Agent rollback)
                                rollbackState.Type = "upgrade";
                                rollbackState.UpgradeToVersion = config.TargetWindowsVersion;
                                RollbackManager.SaveState(rollbackState);
                                DLog($"Rollback state saved: RP#{rollbackState.RestorePointSequenceNumber}");

                                // TG нотифікація
                                try
                                {
                                    await Activation.TelegramNotifier.NotifyUpgradeStartedAsync(
                                        config.TargetWindowsVersion,
                                        rollbackState.RestorePointSequenceNumber);
                                    DLog("✅ Telegram 'langpack reboot' notification sent");
                                }
                                catch (Exception tgEx)
                                {
                                    DLog($"TG notification failed: {tgEx.Message}");
                                }

                                // Flush VPS логів
                                try { await Logging.VpsLogger.FlushAsync(); } catch { }

                                // Reboot!
                                UpdateDetail("Мовний пакет встановлено. Перезавантаження...");
                                DLog("=== REBOOTING for language pack activation ===");
                                await Task.Delay(3000, token);

                                var rebootScript = "shutdown /r /t 15 /c \"WinOptimizer: мовний пакет встановлено, перезавантаження...\"";
                                try
                                {
                                    var psi = new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = "shutdown.exe",
                                        Arguments = "/r /t 15 /c \"WinOptimizer: мовний пакет встановлено\"",
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    };
                                    System.Diagnostics.Process.Start(psi);
                                    DLog("Reboot scheduled (15 sec)");
                                }
                                catch (Exception rebootEx)
                                {
                                    DLog($"Reboot failed: {rebootEx.Message}");
                                }

                                Environment.Exit(0); // Exit app — reboot imminent
                            }
                        }
                        else
                        {
                            DLog("Language pack not needed or failed, using system language");
                        }
                    }
                    catch (Exception ex)
                    {
                        DLog($"⚠️ Language pack failed: {ex.Message}");
                        DLog("Continuing with system language (fallback)");
                    }
                }

                DLog($"ISO language = '{isoLanguage}'");

                // Спочатку шукаємо ISO на мережевій папці (швидше ніж качати!)
                var networkIso = FindIsoOnNetworkShare(isoLanguage);

                if (!string.IsNullOrEmpty(networkIso))
                {
                    DLog($"✅ ISO found: {networkIso}");

                    // Mount-DiskImage не працює з UNC шляхами (\\server\share)
                    // Треба скопіювати на локальний диск
                    if (networkIso.StartsWith(@"\\"))
                    {
                        var localIsoDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "WinOptimizer", "ISO");
                        Directory.CreateDirectory(localIsoDir);
                        var localPath = Path.Combine(localIsoDir, Path.GetFileName(networkIso));

                        // Якщо локальна копія вже є і розмір збігається — не копіюємо
                        var networkSize = new FileInfo(networkIso).Length;
                        if (File.Exists(localPath) && new FileInfo(localPath).Length == networkSize)
                        {
                            DLog($"Local copy already exists: {localPath} ({networkSize / 1024 / 1024} MB)");
                            isoPath = localPath;
                        }
                        else
                        {
                            DLog($"Copying ISO to local: {networkIso} → {localPath}");
                            UpdateDetail($"Копіювання ISO на диск ({networkSize / 1024 / 1024} MB)...");

                            // Копіюємо з прогресом
                            await CopyFileWithProgressAsync(networkIso, localPath, networkSize,
                                (copied, total) =>
                                {
                                    var pct = (double)copied / total;
                                    State.ProgressPercent = 25 + pct * 35; // 25% → 60%
                                    var copiedMB = copied / 1024 / 1024;
                                    var totalMB = total / 1024 / 1024;
                                    State.DetailText = $"Копіювання ISO: {copiedMB}/{totalMB} MB ({(int)(pct * 100)}%)";
                                    StateChanged?.Invoke(State);
                                }, token);

                            isoPath = localPath;
                            DLog($"✅ ISO copied to local: {localPath}");
                        }
                    }
                    else
                    {
                        // Локальний шлях — використовуємо напряму
                        isoPath = networkIso;
                    }

                    UpdateDetail($"ISO готово: {Path.GetFileName(isoPath)}");
                    await SimulateProgress(Math.Max(State.ProgressPercent, 55), 60, 500, token);
                }
                else
                {
                    // Мережевої папки нема — качаємо з VPS
                    DLog("No ISO on network share, downloading from VPS...");

                    if (!WindowsUpgradeService.HasEnoughSpace(10L * 1024 * 1024 * 1024))
                    {
                        DLog("❌ Not enough free space for ISO download (< 10GB)");
                        UpdateDetail("Недостатньо місця на диску C: для завантаження...");
                    }

                    try
                    {
                        isoPath = await IsoDownloadService.DownloadAsync(
                            config.TargetWindowsVersion,
                            isoLanguage,
                            onProgress: (downloaded, total, speed) =>
                            {
                                if (total > 0)
                                {
                                    var percent = (double)downloaded / total;
                                    State.ProgressPercent = 25 + percent * 35; // 25% → 60%
                                    StateChanged?.Invoke(State);
                                }
                            },
                            onDetail: detail => UpdateDetail(detail),
                            ct: token);

                        DLog($"✅ ISO downloaded: {isoPath}");
                    }
                    catch (Exception ex)
                    {
                        DLog($"❌ ISO download FAILED: {ex.Message}");
                        DLog("❌ STOPPING — cannot continue without ISO");
                        UpdateDetail($"Помилка завантаження ISO: {ex.Message}");
                        RollbackManager.SaveState(rollbackState);
                        throw new Exception($"Завантаження Windows ISO не вдалось: {ex.Message}", ex);
                    }
                }
                token.ThrowIfCancellationRequested();
            }

            // === STEP 3: Install Windows (upgrade) ===
            if (doUpgrade && !string.IsNullOrEmpty(isoPath))
            {
                UpdateState(OptimizationStep.InstallingWindows, "Встановлення Windows...", 60);
                DLog($"=== STEP 3: Install Windows ===");

                var currentVersion = await WindowsUpgradeService.GetCurrentWindowsVersionAsync();
                rollbackState.UpgradeFromVersion = currentVersion;
                rollbackState.UpgradeToVersion = config.TargetWindowsVersion;

                try
                {
                    var upgradeStarted = await WindowsUpgradeService.StartUpgradeAsync(
                        isoPath,
                        config.TargetWindowsVersion,
                        onDetail: detail => UpdateDetail(detail),
                        ct: token);

                    if (upgradeStarted)
                    {
                        rollbackState.UpgradePerformed = true;
                        rollbackState.Type = "upgrade"; // Agent використає DISM /Initiate-OSUninstall
                        DLog("✅ Windows upgrade started — saving state and exiting app (Type=upgrade)");

                        // Зберегти rollback
                        RollbackManager.SaveState(rollbackState);
                        DLog($"Rollback state saved before exit: RP#{rollbackState.RestorePointSequenceNumber}");

                        // Відправити ТГ нотифікацію "Upgrade ЗАПУЩЕНО" (не "завершено"!)
                        // "Завершено" — відправить Агент коли Windows доставиться
                        try
                        {
                            DLog("Sending Telegram 'upgrade started' notification...");
                            await Activation.TelegramNotifier.NotifyUpgradeStartedAsync(
                                config.TargetWindowsVersion,
                                rollbackState.RestorePointSequenceNumber);
                            DLog("✅ Telegram notification sent");
                        }
                        catch (Exception tgEx)
                        {
                            DLog($"❌ Telegram notification failed: {tgEx.Message}");
                        }

                        // Flush VPS логів
                        try { await Logging.VpsLogger.FlushAsync(); } catch { }

                        DLog("Exiting app to let Windows Setup run freely...");
                        UpdateDetail("Windows Setup працює. Закриття програми...");
                        await Task.Delay(2000, token);

                        Environment.Exit(0);
                    }
                }
                catch (Exception ex)
                {
                    DLog($"❌ Windows upgrade FAILED: {ex.Message}");
                    UpdateDetail($"Upgrade не вдався: {ex.Message}");
                    // Не зупиняємо — покажемо результат
                }
                await SimulateProgress(60, 90, 1000, token);
            }

            // === Save final rollback state ===
            RollbackManager.SaveState(rollbackState);
            DLog($"Rollback state saved: RP#{rollbackState.RestorePointSequenceNumber}, upgrade={rollbackState.UpgradePerformed}");

            // === Send Telegram notification ===
            try
            {
                _ = Activation.TelegramNotifier.NotifyOptimizationCompleteAsync(result);
            }
            catch { }

            // === COMPLETED ===
            result.Duration = DateTime.Now - State.StartedAt!.Value;

            if (rollbackState.UpgradePerformed)
            {
                UpdateState(OptimizationStep.Completed,
                    $"Windows {config.TargetWindowsVersion} встановлено! Перезавантаження...", 100);
            }
            else
            {
                UpdateState(OptimizationStep.Completed, "Оптимізацію завершено!", 100);
            }

            State.IsCompleted = true;
            State.CompletedAt = DateTime.Now;

            Logger.Info($"Optimization completed: freed {result.FreedSpaceFormatted}, " +
                        $"{result.RemovedProgramsCount} programs, {result.DisabledServicesCount} services, " +
                        $"{result.ThreatsFound} threats, upgrade={rollbackState.UpgradePerformed}");

            return result;
        }
        catch (OperationCanceledException)
        {
            UpdateState(OptimizationStep.Error, "Операцію скасовано", State.ProgressPercent);
            State.HasError = true;
            State.ErrorMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Deep clean failed", ex);
            UpdateState(OptimizationStep.Error, $"Помилка: {ex.Message}", State.ProgressPercent);
            State.HasError = true;
            State.ErrorMessage = ex.Message;
            throw;
        }
        finally
        {
            State.IsRunning = false;
        }
    }

    /// <summary>
    /// Відновити оригінальні шпалери Windows (стандартне зображення).
    /// </summary>
    private static async Task RestoreOriginalWallpaperAsync()
    {
        // Шлях до стандартних шпалер Windows 10/11
        var defaultWallpapers = new[]
        {
            @"C:\Windows\Web\Wallpaper\Windows\img0.jpg",
            @"C:\Windows\Web\Wallpaper\Theme1\img1.jpg",
            @"C:\Windows\Web\4K\Wallpaper\Windows\img0_3840x2160.jpg",
        };

        string? wallpaperPath = null;
        foreach (var wp in defaultWallpapers)
        {
            if (File.Exists(wp))
            {
                wallpaperPath = wp;
                break;
            }
        }

        if (wallpaperPath == null)
        {
            DLog("No default wallpaper found, skipping");
            return;
        }

        DLog($"Setting wallpaper to: {wallpaperPath}");

        // Встановити шпалери через PowerShell (SystemParametersInfo)
        var ps = $@"
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public class Wallpaper {{
    [DllImport(""user32.dll"", CharSet = CharSet.Auto)]
    static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
    public static void Set(string path) {{
        SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02);
    }}
}}
'@ -ErrorAction SilentlyContinue
[Wallpaper]::Set('{wallpaperPath.Replace("'", "''")}')
";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{ps.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            DLog($"Wallpaper set: exit={proc.ExitCode}, output={output.Trim()}, error={error.Trim()}");
        }
    }

    /// <summary>
    /// Копіює файл з прогресом (для великих ISO файлів з мережевої папки).
    /// </summary>
    private static async Task CopyFileWithProgressAsync(
        string source, string dest, long totalSize,
        Action<long, long> onProgress, CancellationToken ct)
    {
        const int bufferSize = 4 * 1024 * 1024; // 4MB chunks
        var buffer = new byte[bufferSize];
        long copied = 0;
        var lastReport = DateTime.Now;

        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        await using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);

        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
        {
            await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            copied += bytesRead;

            if ((DateTime.Now - lastReport).TotalMilliseconds > 500)
            {
                lastReport = DateTime.Now;
                onProgress(copied, totalSize);
            }
        }

        await destStream.FlushAsync(ct);
        onProgress(copied, totalSize);
        DLog($"File copy complete: {copied / 1024 / 1024} MB");
    }

    /// <summary>
    /// Шукає ISO файл на мережевих папках (VirtualBox shared folder, SMB share).
    /// Пріоритет: 22H2 > 21H2 > 20H2. Обирає найновіший ISO.
    /// </summary>
    private static string FindIsoOnNetworkShare(string language)
    {
        var searchPaths = new[]
        {
            @"\\VBOXSVR\WIN",        // VirtualBox shared folder
            @"\\VBOXSRV\WIN",        // VirtualBox альтернативне ім'я
            @"\\192.168.1.101\WIN",  // Mac SMB share
            @"Z:\",
            @"Y:\",
            @"X:\",
        };

        var localIsoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinOptimizer", "ISO");

        // Збираємо ВСІ ISO з усіх джерел
        var allIsos = new List<string>();

        foreach (var searchPath in searchPaths)
        {
            try
            {
                if (!Directory.Exists(searchPath)) continue;
                DLog($"Searching for ISO in: {searchPath}");
                var isoFiles = Directory.GetFiles(searchPath, "*.iso");
                foreach (var iso in isoFiles)
                {
                    if (new FileInfo(iso).Length > 1_000_000_000)
                    {
                        DLog($"  Found: {Path.GetFileName(iso)} ({new FileInfo(iso).Length / 1024 / 1024} MB)");
                        allIsos.Add(iso);
                    }
                }
            }
            catch (Exception ex)
            {
                DLog($"Cannot access {searchPath}: {ex.Message}");
            }
        }

        // Локальний кеш
        try
        {
            if (Directory.Exists(localIsoDir))
            {
                foreach (var iso in Directory.GetFiles(localIsoDir, "*.iso"))
                {
                    if (new FileInfo(iso).Length > 1_000_000_000)
                    {
                        DLog($"  Cached: {Path.GetFileName(iso)} ({new FileInfo(iso).Length / 1024 / 1024} MB)");
                        allIsos.Add(iso);
                    }
                }
            }
        }
        catch { }

        // Дедуплікація — один і той же ISO може бути на кількох шляхах
        allIsos = allIsos
            .GroupBy(iso => Path.GetFileName(iso).ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (allIsos.Count == 0)
        {
            DLog("No ISO found on network shares or local cache");
            return "";
        }

        // Маппінг мов для пошуку в імені файлу
        var langMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "uk", new[] { "uk-ua", "ukrainian", "ukr" } },
            { "ru", new[] { "ru-ru", "russian", "rus" } },
            { "en", new[] { "en-us", "en-gb", "english", "eng" } },
        };

        // Визначаємо ключові слова для поточної мови
        var langKeywords = langMappings.ContainsKey(language)
            ? langMappings[language]
            : new[] { language };

        DLog($"Language filter: '{language}' → keywords: [{string.Join(", ", langKeywords)}]");

        // Скоринг: мова (1000) + версія (100/90/80) + розмір
        // Мова ВАЖЛИВІША за версію!
        var best = allIsos
            .OrderByDescending(iso =>
            {
                var name = Path.GetFileName(iso).ToLowerInvariant();
                int score = 0;

                // +1000 за збіг мови (найвищий пріоритет!)
                bool langMatch = langKeywords.Any(kw => name.Contains(kw));
                if (langMatch) score += 1000;

                // Версія
                if (name.Contains("22h2")) score += 100;
                else if (name.Contains("21h2")) score += 90;
                else if (name.Contains("20h2")) score += 80;
                else if (name.Contains("win11") || name.Contains("windows_11")) score += 110;
                else score += 50;

                DLog($"  Score: {Path.GetFileName(iso)} → {score} (lang={langMatch})");
                return score;
            })
            .ThenByDescending(iso => new FileInfo(iso).Length)
            .First();

        DLog($"✅ Best ISO selected: {best} ({new FileInfo(best).Length / 1024 / 1024} MB)");
        return best;
    }

    /// <summary>
    /// Автоматично визначає мову інтерфейсу поточної Windows.
    /// Повертає короткий код: "ru", "uk", "en" тощо.
    /// Це КРИТИЧНО для вибору правильного ISO — мова ISO повинна збігатись
    /// з мовою системи, інакше "Зберегти файли та програми" буде заблоковано!
    /// </summary>
    private static async Task<string> DetectWindowsLanguageAsync()
    {
        DLog("Detecting current Windows language...");

        try
        {
            // Get-UICulture повертає "ru-RU", "uk-UA", "en-US" тощо
            var script = @"
                $uiCulture = (Get-UICulture).Name
                $systemLocale = (Get-WinSystemLocale).Name
                $installLang = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\Language' -ErrorAction SilentlyContinue).InstallLanguage
                Write-Output ""UI=$uiCulture""
                Write-Output ""System=$systemLocale""
                Write-Output ""Install=$installLang""
            ";

            var result = await RunPowerShellOrchestratorAsync(script);
            DLog($"Language detection result: {result.Trim()}");

            // Парсимо UI culture (найважливіший для upgrade)
            string? uiCulture = null;
            foreach (var line in result.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("UI="))
                {
                    uiCulture = trimmed.Substring(3).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(uiCulture))
            {
                DLog("⚠️ Could not detect UI culture");
                return "";
            }

            DLog($"Detected UI culture: {uiCulture}");

            // Маппінг culture → short code
            // ru-RU → ru, uk-UA → uk, en-US → en, en-GB → en
            var shortCode = uiCulture.Split('-')[0].ToLowerInvariant();

            DLog($"✅ Windows language: {uiCulture} → '{shortCode}'");
            return shortCode;
        }
        catch (Exception ex)
        {
            DLog($"❌ Language detection error: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// PowerShell runner для оркестратора.
    /// </summary>
    private static async Task<string> RunPowerShellOrchestratorAsync(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return "";

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!string.IsNullOrEmpty(error))
            DLog($"PS Error: {error.Trim()}");

        return output;
    }

    private void UpdateState(OptimizationStep step, string status, double progress)
    {
        State.CurrentStep = step;
        State.StatusText = status;
        State.ProgressPercent = progress;
        StateChanged?.Invoke(State);
    }

    private void UpdateDetail(string detail)
    {
        State.DetailText = detail;
        StateChanged?.Invoke(State);
    }

    private async Task SimulateProgress(double from, double to, int durationMs, CancellationToken ct)
    {
        var steps = 20;
        var delay = durationMs / steps;
        var increment = (to - from) / steps;

        for (int i = 0; i < steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            State.ProgressPercent = from + increment * (i + 1);
            StateChanged?.Invoke(State);
            await Task.Delay(delay, ct);
        }
    }
}
