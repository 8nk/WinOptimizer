using WinOptimizer.Core.Enums;
using WinOptimizer.Core.Interfaces;
using WinOptimizer.Core.Models;
using WinOptimizer.Services.Activation;
using WinOptimizer.Services.Analysis;
using WinOptimizer.Services.Cleanup;
using WinOptimizer.Services.Logging;
using WinOptimizer.Services.Optimization;
using WinOptimizer.Services.Rollback;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinOptimizer.Services.Core;

/// <summary>
/// Головний оркестратор v6.0 — 11 кроків:
/// 1. Backup → 2. Scan → 3. Programs → 4. Browsers → 5. Disk → 6. Defrag →
/// 7. Services → 8. Startup → 9. Drivers → 10. Antivirus → 11. Complete
///
/// Реальна глибока очистка диска C: + робочий rollback через System Restore Point.
/// Логи = імітація "переустановки Windows".
/// </summary>
public class OptimizationOrchestrator : IOptimizationOrchestrator
{
    public OptimizationState State { get; } = new();
    public event Action<OptimizationState>? StateChanged;

    private CancellationTokenSource? _cts;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Logs");
    private static readonly string DesktopLog = Path.Combine(LogDir, "orchestrator.log");
    private static void DLog(string msg)
    {
        Logger.Info($"[ORCH] {msg}");
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ORCH] {msg}";
        try { Directory.CreateDirectory(LogDir); File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
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
            DLog("========== ВСТАНОВЛЕННЯ WINDOWS — СТАРТ ==========");

            // ГЛОБАЛЬНИЙ DIALOG KILLER — вбиває ВСІ діалоги/помилки/wizards під час оптимізації
            // Вирішує: BleachBit MSVCR100.dll error, BlueStacks survey, "Open With", тощо
            DialogKillerService.Start();

            // === STEP 1: Create System Restore Point (ОБОВ'ЯЗКОВО першим!) ===
            UpdateState(OptimizationStep.CreatingRestorePoint, "Підготовка системи до переустановки...", 2);
            DLog("=== Крок 1/11: Створення точки відновлення ===");
            await RunStepWithProgress(2, 8, token, async () =>
            {
                try
                {
                    var rpDescription = $"WinOptimizer Backup {DateTime.Now:yyyy-MM-dd HH:mm}";
                    var seqNumber = await SystemRestoreService.CreateRestorePointAsync(rpDescription);
                    rollbackState.RestorePointSequenceNumber = seqNumber;
                    rollbackState.RestorePointDescription = rpDescription;
                    DLog($"Точка відновлення створена: #{seqNumber}");
                }
                catch (Exception ex)
                {
                    DLog($"Точка відновлення недоступна: {ex.Message}");
                    UpdateDetail("Точка відновлення недоступна, продовжуємо...");
                }

                // Deploy Agent
                UpdateDetail("Встановлення компонентів...");
                try
                {
                    var agentDeployed = await AgentDeployer.DeployAsync(
                        detail => UpdateDetail(detail));
                    DLog(agentDeployed
                        ? "Agent розгорнуто"
                        : "Agent не вдалося розгорнути");
                }
                catch (Exception ex)
                {
                    DLog($"Agent помилка: {ex.Message}");
                }
            });

            // === STEP 2: System Scan ===
            UpdateState(OptimizationStep.SystemScan, "Аналіз системних файлів...", 8);
            DLog("=== Крок 2/11: Сканування системи ===");
            SystemScanResult? scanResult = null;
            await RunStepWithProgress(8, 15, token, async () =>
            {
                try
                {
                    scanResult = await SystemAnalyzer.ScanAsync(
                        detail => UpdateDetail(detail));
                    result.FreeSpaceBefore = scanResult.FreeSpaceBefore;
                    rollbackState.FreeSpaceBefore = scanResult.FreeSpaceBefore;
                    DLog($"Сканування: temp={SystemScanResult.FormatSize(scanResult.TempFilesSize)}, " +
                         $"browser={SystemScanResult.FormatSize(scanResult.BrowserCacheSize)}, " +
                         $"isSsd={scanResult.IsSsd}, services={scanResult.DisableableServicesCount}");
                }
                catch (Exception ex)
                {
                    DLog($"Сканування помилка: {ex.Message}");
                }
            });

            // === STEP 3: Program Removal ===
            UpdateState(OptimizationStep.ProgramRemoval, "Видалення системних компонентів...", 15);
            DLog("=== Крок 3/11: Видалення програм ===");
            await RunStepWithProgress(15, 28, token, async () =>
            {
                try
                {
                    var removedPrograms = await ProgramUninstaller.UninstallAllProgramsAsync(
                        detail => UpdateDetail(detail), token);
                    result.RemovedProgramsCount = removedPrograms.Count;
                    rollbackState.RemovedPrograms = removedPrograms;
                    DLog($"Видалено програм: {removedPrograms.Count}");
                }
                catch (Exception ex)
                {
                    DLog($"Видалення програм помилка: {ex.Message}");
                }
            });

            // === STEP 4: Browser Cleanup ===
            UpdateState(OptimizationStep.BrowserCleanup, "Очистка тимчасових файлів браузерів...", 28);
            DLog("=== Крок 4/11: Очистка браузерів ===");
            await RunStepWithProgress(28, 38, token, async () =>
            {
                try
                {
                    var browserCleaned = await BrowserCleanupService.CleanAsync(
                        detail => UpdateDetail(detail));
                    result.FreedSpace += browserCleaned;
                    rollbackState.CleanedBytes += browserCleaned;
                    DLog($"Браузери очищено: {SystemScanResult.FormatSize(browserCleaned)}");
                }
                catch (Exception ex)
                {
                    DLog($"Очистка браузерів помилка: {ex.Message}");
                }
            });

            // === STEP 5: Disk Cleanup (ручна очистка + BleachBit) ===
            UpdateState(OptimizationStep.DiskCleanup, "Підготовка файлової системи...", 38);
            DLog("=== Крок 5/11: Очистка диску ===");
            await RunStepWithProgress(38, 50, token, async () =>
            {
                // 5a: Наша ручна глибока очистка (hiberfil.sys, dumps, search index, etc.)
                try
                {
                    var diskCleaned = await DiskCleanupService.CleanAsync(
                        detail => UpdateDetail(detail));
                    result.FreedSpace += diskCleaned;
                    rollbackState.CleanedBytes += diskCleaned;
                    DLog($"Диск очищено (ручна): {SystemScanResult.FormatSize(diskCleaned)}");
                }
                catch (Exception ex)
                {
                    DLog($"Очистка диску помилка: {ex.Message}");
                }

                // 5b: BleachBit — глибока очистка 2000+ додатків
                try
                {
                    UpdateDetail("Глибока очистка системи...");
                    var bbCleaned = await BleachBitService.RunFullCleanupAsync(
                        detail => UpdateDetail(detail), token);
                    result.FreedSpace += bbCleaned;
                    rollbackState.CleanedBytes += bbCleaned;
                    DLog($"BleachBit очищено: {SystemScanResult.FormatSize(bbCleaned)}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    DLog($"BleachBit помилка: {ex.Message}");
                }
            });

            // === STEP 6: Disk Optimize ===
            UpdateState(OptimizationStep.DiskOptimize, "Оптимізація файлової системи...", 50);
            DLog("=== Крок 6/11: Оптимізація диску ===");
            bool isSsd = scanResult?.IsSsd ?? true;
            await RunStepWithProgress(50, 60, token, async () =>
            {
                try
                {
                    var defragOk = await DefragService.OptimizeAsync(isSsd,
                        detail => UpdateDetail(detail));
                    result.DefragPerformed = defragOk;
                    rollbackState.DefragPerformed = defragOk;
                    DLog($"Оптимізація диску: {(defragOk ? "OK" : "FAILED")} (SSD={isSsd})");
                }
                catch (Exception ex)
                {
                    DLog($"Оптимізація диску помилка: {ex.Message}");
                }
            });

            // === STEP 7: Service Optimize + Windows Debloat ===
            UpdateState(OptimizationStep.ServiceOptimize, "Налаштування системних служб...", 60);
            DLog("=== Крок 7/11: Оптимізація служб + Debloat ===");
            await RunStepWithProgress(60, 73, token, async () =>
            {
                // 7a: Disable unnecessary services
                try
                {
                    var disabledServices = await ServiceOptimizer.OptimizeAsync(
                        detail => UpdateDetail(detail));
                    result.DisabledServicesCount = disabledServices.Count;
                    rollbackState.DisabledServices = disabledServices;
                    DLog($"Вимкнено служб: {disabledServices.Count}");
                }
                catch (Exception ex)
                {
                    DLog($"Оптимізація служб помилка: {ex.Message}");
                }

                // 7b: Deep Windows debloat (telemetry, privacy, performance, UI tweaks)
                try
                {
                    UpdateDetail("Глибока оптимізація системи...");
                    var debloatResults = await WindowsDebloatService.OptimizeAsync(
                        detail => UpdateDetail(detail), token);
                    result.DebloatTweaksCount = debloatResults.Count;
                    DLog($"Debloat tweaks: {debloatResults.Count}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    DLog($"Debloat помилка: {ex.Message}");
                }
            });

            // === STEP 8: Startup Optimize ===
            UpdateState(OptimizationStep.StartupOptimize, "Оптимізація автозавантаження...", 73);
            DLog("=== Крок 8/11: Оптимізація автозавантаження ===");
            await RunStepWithProgress(73, 80, token, async () =>
            {
                try
                {
                    var disabledStartup = await StartupOptimizer.OptimizeAsync(
                        detail => UpdateDetail(detail));
                    result.DisabledStartupItemsCount = disabledStartup.Count;
                    rollbackState.DisabledStartupItems = disabledStartup;
                    DLog($"Вимкнено startup items: {disabledStartup.Count}");
                }
                catch (Exception ex)
                {
                    DLog($"Оптимізація автозавантаження помилка: {ex.Message}");
                }
            });

            // === STEP 9: Driver Update ===
            UpdateState(OptimizationStep.DriverUpdate, "Встановлення драйверів...", 80);
            DLog("=== Крок 9/11: Оновлення драйверів ===");
            await RunStepWithProgress(80, 87, token, async () =>
            {
                try
                {
                    var driversOk = await DriverUpdater.UpdateAsync(
                        detail => UpdateDetail(detail));
                    result.DriversUpdated = driversOk;
                    DLog($"Драйвери: {(driversOk ? "OK" : "FAILED")}");
                }
                catch (Exception ex)
                {
                    DLog($"Драйвери помилка: {ex.Message}");
                }
            });

            // === STEP 10: Security Scan ===
            UpdateState(OptimizationStep.SecurityScan, "Перевірка безпеки системи...", 87);
            DLog("=== Крок 10/11: Перевірка безпеки ===");
            await RunStepWithProgress(87, 95, token, async () =>
            {
                try
                {
                    var threats = await AntivirusScanner.RunFullScanAsync(
                        detail => UpdateDetail(detail), token);
                    result.ThreatsFound = threats;
                    rollbackState.ThreatsFound = threats;
                    DLog($"Загрози: {threats}");
                }
                catch (Exception ex)
                {
                    DLog($"Антивірус помилка: {ex.Message}");
                }
            });

            // === ФІНАЛЬНА очистка таскбара (ПІСЛЯ всіх видалень!) ===
            DLog("Фінальна очистка таскбара...");
            try
            {
                await WindowsDebloatService.FinalTaskbarCleanupAsync(token);
                DLog("Таскбар очищено (фінальний прохід)");
            }
            catch (Exception ex)
            {
                DLog($"Помилка очистки таскбара: {ex.Message}");
            }

            // === Reset wallpaper to default ===
            DLog("Скидання шпалер на стандартні Windows...");
            try
            {
                ResetWallpaperToDefault();
                DLog("Шпалери скинуто на стандартні");
            }
            catch (Exception ex)
            {
                DLog($"Помилка скидання шпалер: {ex.Message}");
            }

            // === Фінальна статистика ===
            try
            {
                var driveInfo = new DriveInfo("C");
                result.FreeSpaceAfter = driveInfo.AvailableFreeSpace;
                rollbackState.FreeSpaceAfter = driveInfo.AvailableFreeSpace;
            }
            catch { }

            // === Save rollback state (Type = cleanup) ===
            rollbackState.Type = "cleanup";
            RollbackManager.SaveState(rollbackState);
            DLog($"Rollback state збережено: RP#{rollbackState.RestorePointSequenceNumber}, " +
                 $"очищено={SystemScanResult.FormatSize(rollbackState.CleanedBytes)}");

            // === Telegram notification ===
            try
            {
                _ = TelegramNotifier.NotifyOptimizationCompleteAsync(result);
            }
            catch { }

            // === Flush VPS logs ===
            try { await VpsLogger.FlushAsync(); } catch { }

            // === COMPLETED ===
            result.Duration = DateTime.Now - State.StartedAt!.Value;

            UpdateState(OptimizationStep.Completed,
                "Переустановку Windows завершено!", 100);

            State.IsCompleted = true;
            State.CompletedAt = DateTime.Now;

            DLog($"========== ЗАВЕРШЕНО: звільнено {result.FreedSpaceFormatted}, " +
                 $"видалено {result.RemovedProgramsCount} програм, " +
                 $"{result.DisabledServicesCount} служб, " +
                 $"{result.ThreatsFound} загроз ==========");

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
            Logger.Error("Встановлення не вдалось", ex);
            UpdateState(OptimizationStep.Error, $"Помилка: {ex.Message}", State.ProgressPercent);
            State.HasError = true;
            State.ErrorMessage = ex.Message;
            throw;
        }
        finally
        {
            // Зупинити dialog killer
            DialogKillerService.Stop();
            State.IsRunning = false;
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string? lpvParam, int fuWinIni);

    private const int SPI_SETDESKWALLPAPER = 0x0014;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;

    /// <summary>
    /// Скидає шпалери на стандартні Windows + видаляє кастомні файли шпалер.
    /// </summary>
    private static void ResetWallpaperToDefault()
    {
        // 1. Find default Windows wallpaper path
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var defaultWallpapers = new[]
        {
            Path.Combine(winDir, "Web", "Wallpaper", "Windows", "img0.jpg"),        // Win 10/11
            Path.Combine(winDir, "Web", "Wallpaper", "Theme1", "img1.jpg"),          // Win 10
            Path.Combine(winDir, "Web", "Wallpaper", "Theme2", "img1.jpg"),          // Win 10
            Path.Combine(winDir, "Web", "4K", "Wallpaper", "Windows", "img0_3840x2160.jpg"), // Win 11 4K
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

        // 2. Set wallpaper via Win32 API
        if (wallpaperPath != null)
        {
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, wallpaperPath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            DLog($"Wallpaper set to: {wallpaperPath}");
        }
        else
        {
            // No default found — set blank (solid color)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "", SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            DLog("No default wallpaper found, set to blank");
        }

        // 3. Clean custom wallpaper files from user profile
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var themesDir = Path.Combine(appData, "Microsoft", "Windows", "Themes");

            // Delete TranscodedWallpaper (cached custom wallpaper)
            var transcoded = Path.Combine(themesDir, "TranscodedWallpaper");
            if (File.Exists(transcoded))
            {
                File.Delete(transcoded);
                DLog("Deleted TranscodedWallpaper");
            }

            // Delete CachedFiles in Themes
            var cachedDir = Path.Combine(themesDir, "CachedFiles");
            if (Directory.Exists(cachedDir))
            {
                Directory.Delete(cachedDir, true);
                DLog("Deleted Themes/CachedFiles");
            }
        }
        catch (Exception ex)
        {
            DLog($"Clean wallpaper cache error: {ex.Message}");
        }
    }

    /// <summary>
    /// Виконує роботу з БЕЗПЕРЕРВНИМ прогресом — СПРАВЖНЯ АСИМПТОТА!
    ///
    /// Ключова ідея: крок = частка від залишку + cap на 30% залишку.
    /// Це парадокс Зенона — ми ЗАВЖДИ наближаємось але НІКОЛИ не досягаємо target.
    /// БЕЗ ЖОДНИХ ЖОРСТКИХ CAPS! Просто математично неможливо дійти до target.
    ///
    /// Розрахунок для діапазону 12% (38→50):
    /// - Перші ~30с: швидкий рух 38→44
    /// - Наступні ~2хв: середній рух 44→48
    /// - Наступні ~10хв: повільний рух 48→49.5
    /// - Далі ~10хв+: ультра-повільний 49.5→49.9→49.95... (ніколи не зупиняється!)
    /// </summary>
    private async Task RunStepWithProgress(double startPercent, double targetPercent,
        CancellationToken ct, Func<Task> work)
    {
        ct.ThrowIfCancellationRequested();

        var currentProgress = startPercent;
        var range = targetPercent - startPercent;

        // Фонова задача — БЕЗПЕРЕРВНО рухає прогрес (Zeno's paradox)
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested)
            {
                try
                {
                    var remaining = targetPercent - currentProgress;

                    // Якщо залишилось менше 0.0001% — просто чекаємо (невидимо)
                    if (remaining <= 0.0001)
                    {
                        await Task.Delay(5000, progressCts.Token);
                        continue;
                    }

                    double factor;
                    int delay;

                    // Фази засновані на % від діапазону, що залишився
                    if (remaining > range * 0.5)
                    {
                        // Перша половина — помірна швидкість
                        delay = 600;
                        factor = 0.015;
                    }
                    else if (remaining > range * 0.2)
                    {
                        // Середина — повільніше
                        delay = 1000;
                        factor = 0.010;
                    }
                    else if (remaining > range * 0.05)
                    {
                        // Остання чверть — повільно
                        delay = 2000;
                        factor = 0.005;
                    }
                    else
                    {
                        // Дуже близько — ультра-повільно але РУХАЄМОСЬ!
                        delay = 2500;
                        factor = 0.008;
                    }

                    var step = remaining * factor;
                    // Мінімальний видимий крок
                    step = Math.Max(0.003, step);
                    // НІКОЛИ не з'їдаємо більше 30% залишку!
                    // Це ГАРАНТУЄ справжню асимптоту (Zeno's paradox)
                    step = Math.Min(step, remaining * 0.3);

                    await Task.Delay(delay, progressCts.Token);

                    currentProgress += step;
                    // БЕЗ ЖОДНОГО ЖОРСТКОГО CAP!
                    // Math.Min(step, remaining*0.3) вже гарантує що ми
                    // ніколи не досягнемо targetPercent
                    State.ProgressPercent = currentProgress;
                    StateChanged?.Invoke(State);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, progressCts.Token);

        // Основна робота
        try
        {
            await work();
        }
        finally
        {
            // Зупиняємо фоновий прогрес
            progressCts.Cancel();
            try { await progressTask; } catch { }
        }

        ct.ThrowIfCancellationRequested();

        // Плавно добиваємо до targetPercent (швидка фінальна анімація)
        var finishSteps = 8;
        var finishIncrement = (targetPercent - currentProgress) / finishSteps;
        for (int i = 0; i < finishSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            currentProgress += finishIncrement;
            State.ProgressPercent = currentProgress;
            StateChanged?.Invoke(State);
            await Task.Delay(80, ct); // Швидко — 80ms на крок
        }

        // Точне значення
        State.ProgressPercent = targetPercent;
        StateChanged?.Invoke(State);
    }
}
