using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using WinOptimizer.Core.Enums;
using WinOptimizer.Core.Models;
using WinOptimizer.Services.Activation;
using WinOptimizer.Services.Analysis;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.ViewModels;

/// <summary>
/// Log entry for the installation screen with fade-in animation support
/// </summary>
public class LogEntry : ObservableObject
{
    public string Icon { get; set; } = "";
    public string Text { get; set; } = "";
    public IBrush IconBrush { get; set; } = new SolidColorBrush(Color.Parse("#888888"));

    private double _opacity;
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly OptimizationOrchestrator _orchestrator;
    private string _language = "uk";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isOptimizing = false;

    [ObservableProperty]
    private OptimizationStep _currentOptimizationStep = OptimizationStep.NotStarted;

    [ObservableProperty]
    private int _currentStepIndex = 0;

    [ObservableProperty]
    private bool _isCompleted = false;

    [ObservableProperty]
    private bool _hasError = false;

    /// <summary>
    /// Фаза 2 — вікно зменшується, драйвери + фінал.
    /// Fullscreen (Фаза 1) → Windowed (Фаза 2) при переході до DriverUpdate.
    /// </summary>
    [ObservableProperty]
    private bool _isPhase2 = false;

    [ObservableProperty]
    private bool _showPercentage = false;

    [ObservableProperty]
    private string _progressPercentageText = "";

    [ObservableProperty]
    private double _progressPercent = 0;

    [ObservableProperty]
    private string _systemInfoText = "";

    [ObservableProperty]
    private bool _canStart = true;

    // Activation
    [ObservableProperty]
    private bool _showActivationScreen = true;

    [ObservableProperty]
    private string _activationCode = "";

    [ObservableProperty]
    private string _activationStatus = "";

    [ObservableProperty]
    private bool _hasActivationStatus = false;

    [ObservableProperty]
    private bool _canActivate = true;

    [ObservableProperty]
    private string _activateButtonText = "Activate";

    [ObservableProperty]
    private IBrush _activationStatusBrush = new SolidColorBrush(Color.Parse("#B71C1C"));

    [ObservableProperty]
    private bool _showLanguageScreen = false;

    [ObservableProperty]
    private bool _showMainScreen = false;

    // Antivirus removal screen
    [ObservableProperty]
    private bool _showAntivirusScreen = false;

    [ObservableProperty]
    private bool _canContinueFromAntivirus = false;

    // true = хоч один AV зараз видаляється (показуємо попередження про сірий екран)
    public bool IsAnyAntivirusRemoving =>
        DetectedAntiviruses.Any(a => a.IsRemoving);

    public ObservableCollection<AntivirusItemViewModel> DetectedAntiviruses { get; } = new();

    // Localized labels
    [ObservableProperty]
    private string _subtitleText = "";

    [ObservableProperty]
    private string _systemInfoLabel = "";

    [ObservableProperty]
    private string _autoInstallButtonText = "";

    [ObservableProperty]
    private string _closeButtonText = "";

    [ObservableProperty]
    private string _stepDescription = "";

    [ObservableProperty]
    private string _bottomHintText = "";

    // Hardware analysis
    [ObservableProperty]
    private bool _hasHardwareInfo = false;

    [ObservableProperty]
    private string _recommendedWindowsText = "";

    [ObservableProperty]
    private string _recommendationReason = "";

    [ObservableProperty]
    private string _hwidText = "";

    [ObservableProperty]
    private string _currentOsText = "";

    // Phase 2 header — shows "Windows installation complete!" when switching to windowed mode
    public string Phase2HeaderText => _language switch
    {
        "en" => "\u2705 Windows installation complete!",
        "ru" => "\u2705 Установка Windows завершена!",
        _ => "\u2705 Установку Windows завершено!"
    };

    // Result display
    [ObservableProperty]
    private bool _hasResult = false;

    [ObservableProperty]
    private string _resultFreedSpace = "";

    [ObservableProperty]
    private string _resultPrograms = "";

    [ObservableProperty]
    private string _resultThreats = "";

    [ObservableProperty]
    private bool _hasThreatsResult = false;

    [ObservableProperty]
    private string _resultServices = "";

    [ObservableProperty]
    private string _resultStartup = "";

    [ObservableProperty]
    private string _resultDuration = "";

    // Log entries for installation panel
    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    private readonly HashSet<string> _shownMilestones = new();

    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush CyanBrush = new SolidColorBrush(Color.Parse("#00838F"));

    public MainWindowViewModel()
    {
        _orchestrator = new OptimizationOrchestrator();
        _orchestrator.StateChanged += OnStateChanged;
    }

    partial void OnShowActivationScreenChanged(bool value) => UpdateShowMainScreen();
    partial void OnShowLanguageScreenChanged(bool value) => UpdateShowMainScreen();
    partial void OnShowAntivirusScreenChanged(bool value) => UpdateShowMainScreen();
    partial void OnIsOptimizingChanged(bool value) => UpdateShowMainScreen();

    /// <summary>
    /// При переході в Phase 2 — оновити всі responsive розміри для вікна 800×500.
    /// </summary>
    partial void OnIsPhase2Changed(bool value)
    {
        OnPropertyChanged(nameof(Phase2HeaderText));
    }

    private void UpdateShowMainScreen()
    {
        ShowMainScreen = !ShowActivationScreen && !ShowLanguageScreen && !ShowAntivirusScreen && !IsOptimizing && !IsCompleted && !HasError;
    }

    #region Activation

    [RelayCommand]
    private async Task ActivateAsync()
    {
        Logger.Info($"[UI] ActivateAsync called, code='{ActivationCode}'");

        if (string.IsNullOrWhiteSpace(ActivationCode))
        {
            Logger.Warn("[UI] ActivationCode is empty");
            ActivationStatus = "Enter activation code";
            ActivationStatusBrush = new SolidColorBrush(Color.Parse("#B71C1C"));
            HasActivationStatus = true;
            return;
        }

        // Normalize input: extract digits, reformat
        var rawDigits = new string(ActivationCode.Where(char.IsDigit).ToArray());
        Logger.Info($"[UI] rawDigits='{rawDigits}' length={rawDigits.Length}");
        string trimmed;

        if (rawDigits.Length == 12)
        {
            // Reformat to XX-XX-XX-XX-XX-XX
            var codeParts = new string[6];
            for (int i = 0; i < 6; i++)
                codeParts[i] = rawDigits.Substring(i * 2, 2);
            trimmed = string.Join("-", codeParts);
            Logger.Info($"[UI] Reformatted to '{trimmed}'");
        }
        else if (ActivationCode.Trim().StartsWith("WF-", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = ActivationCode.Trim();
            Logger.Info($"[UI] WF- prefix code: '{trimmed}'");
        }
        else
        {
            Logger.Warn($"[UI] Bad format: rawDigits.Length={rawDigits.Length}");
            ActivationStatus = "Wrong format. Expected: 00-00-00-00-00-00";
            ActivationStatusBrush = new SolidColorBrush(Color.Parse("#B71C1C"));
            HasActivationStatus = true;
            return;
        }

        CanActivate = false;
        ActivateButtonText = "Validating...";
        ActivationStatus = "";
        HasActivationStatus = false;

        try
        {
            await Task.Delay(500);
            var result = await TokenService.ValidateWithReasonAsync(trimmed);

            if (result.IsValid)
            {
                ActivationStatus = "Activated!";
                ActivationStatusBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
                HasActivationStatus = true;

                var hwid = TelegramNotifier.GetHardwareId();
                _ = TokenService.MarkTokenUsedAsync(trimmed, hwid);
                _ = TelegramNotifier.NotifyActivationAsync(trimmed);
                _ = TelegramNotifier.RegisterOnVpsAsync(trimmed);

                await Task.Delay(800);
                ShowActivationScreen = false;

                // Перевірити антивіруси перед мовним екраном
                await CheckAndShowAntivirusScreenAsync();
            }
            else
            {
                ActivationStatus = result.Reason switch
                {
                    "expired" => "Code expired. Request a new one.",
                    "already used" => "Code already used.",
                    "not found" => "Code not found. Check and try again.",
                    "bad format" => "Wrong format. Expected: 00-00-00-00-00-00",
                    "network error" => "Server unavailable. Check internet connection.",
                    "timeout" => "Server not responding. Try again.",
                    "server error" => "Server error. Try again later.",
                    _ => $"Invalid code ({result.Reason})"
                };
                ActivationStatusBrush = new SolidColorBrush(Color.Parse("#B71C1C"));
                HasActivationStatus = true;
                CanActivate = true;
                ActivateButtonText = "Activate";
            }
        }
        catch
        {
            ActivationStatus = "Connection error. Check internet.";
            ActivationStatusBrush = new SolidColorBrush(Color.Parse("#B71C1C"));
            HasActivationStatus = true;
            CanActivate = true;
            ActivateButtonText = "Activate";
        }
    }

    #endregion

    #region Antivirus Detection

    private CancellationTokenSource? _avPollingCts;

    private async Task CheckAndShowAntivirusScreenAsync()
    {
        var antiviruses = await Task.Run(() => AntivirusDetectionService.Detect());

        if (antiviruses.Count == 0)
        {
            // Антивірусів немає — одразу на мовний екран
            ShowLanguageScreen = true;
            return;
        }

        // Заповнити список
        DetectedAntiviruses.Clear();
        foreach (var av in antiviruses)
            DetectedAntiviruses.Add(new AntivirusItemViewModel(av));

        CanContinueFromAntivirus = false;
        ShowAntivirusScreen = true;

        // Запустити polling — перевіряємо кожні 3 секунди чи вже видалено
        _avPollingCts?.Cancel();
        _avPollingCts = new CancellationTokenSource();
        _ = StartAvPollingAsync(_avPollingCts.Token);
    }

    private async Task StartAvPollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(3000, ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested) break;

            await CheckAllAvsAsync();

            if (CanContinueFromAntivirus) break;
        }
    }

    /// <summary>
    /// Перевіряє кожен AV чи ще встановлений. Якщо зник — маркує видаленим.
    /// </summary>
    private async Task CheckAllAvsAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in DetectedAntiviruses)
            {
                if (item.IsRemoved) continue;
                var stillInstalled = AntivirusDetectionService.IsStillInstalled(item.Data);
                if (!stillInstalled)
                {
                    item.MarkRemoved();
                    OnPropertyChanged(nameof(IsAnyAntivirusRemoving));
                }
            }
            // "Продовжити →" активується — юзер сам натискає (БЕЗ авто-продовження!)
            CanContinueFromAntivirus = DetectedAntiviruses.All(a => a.IsRemoved);
        });
    }

    [RelayCommand]
    private void RemoveAntivirus(AntivirusItemViewModel av)
    {
        // Дизейблимо одразу — щоб не натиснули 2-3 рази!
        if (av.IsRemoving || av.IsRemoved) return;
        av.IsRemoving = true;
        OnPropertyChanged(nameof(IsAnyAntivirusRemoving));

        // Запускаємо деінсталятор і стежимо за процесом
        var proc = AntivirusDetectionService.LaunchUninstaller(av.Data);

        // Фонова задача: чекаємо поки деінсталятор закриється → одразу скануємо
        if (proc != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Чекаємо максимум 15 хвилин — деінсталяція не буває довше
                    proc.WaitForExit(15 * 60 * 1000);
                }
                catch { }

                // Даємо Windows 2 секунди щоб почистити registry
                await Task.Delay(2000);

                // Одразу перевіряємо чи AV видалено
                await CheckAllAvsAsync();
            });
        }
    }

    /// <summary>
    /// Ручна кнопка "✓ Я видалив" — юзер може натиснути якщо авто-скан не спрацював.
    /// Запускає негайне сканування.
    /// </summary>
    [RelayCommand]
    private async Task ManualCheckRemoved(AntivirusItemViewModel av)
    {
        if (av.IsRemoved) return;
        av.StatusIcon = "🔍"; // Показуємо що йде перевірка

        await Task.Delay(500); // Мінімальна пауза для UX

        var stillInstalled = await Task.Run(() => AntivirusDetectionService.IsStillInstalled(av.Data));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!stillInstalled)
            {
                av.MarkRemoved();
                OnPropertyChanged(nameof(IsAnyAntivirusRemoving));
                CanContinueFromAntivirus = DetectedAntiviruses.All(a => a.IsRemoved);
            }
            else
            {
                // AV ще є — повертаємо стан "видалення"
                av.StatusIcon = "⏳";
            }
        });
    }

    [RelayCommand]
    private void ContinueFromAntivirus()
    {
        _avPollingCts?.Cancel();
        ShowAntivirusScreen = false;
        ShowLanguageScreen = true;
    }

    #endregion

    #region Language

    [RelayCommand]
    private void SelectLanguage(string lang)
    {
        _language = lang;
        ApplyLanguage();
        ShowLanguageScreen = false;
        ShowMainScreen = true;

        // Show loading text while hardware analysis runs
        StatusMessage = L("Аналіз системи...", "Анализ системы...", "Analyzing system...");
        SystemInfoText = L(
            "Зачекайте, аналізуємо обладнання...",
            "Подождите, анализируем оборудование...",
            "Please wait, analyzing hardware...");
        CanStart = false;

        // Get HWID
        HwidText = $"ID: {TelegramNotifier.GetHardwareId()}";

        // Start hardware analysis in background
        _ = RunHardwareAnalysisAsync();
    }

    private async Task RunHardwareAnalysisAsync()
    {
        try
        {
            var hw = await HardwareAnalyzer.AnalyzeAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Build system info text
                var ramGb = hw.RamMB / 1024.0;
                var diskType = hw.IsSsd ? "SSD" : "HDD";
                var tpmText = hw.HasTpm2 ? "TPM 2.0" : L("Немає TPM 2.0", "Нет TPM 2.0", "No TPM 2.0");
                var uefiText = hw.IsUefi ? "UEFI" : "Legacy BIOS";

                SystemInfoText = $"CPU: {hw.CpuName}\n" +
                                 $"RAM: {ramGb:F0} GB\n" +
                                 $"{L("Диск", "Диск", "Disk")}: {diskType} {hw.DiskSizeGB} GB\n" +
                                 $"GPU: {hw.GpuName}\n" +
                                 $"{tpmText} | {uefiText}";

                CurrentOsText = $"{L("Поточна ОС", "Текущая ОС", "Current OS")}: {hw.CurrentWindows}";

                // Hardware info
                HasHardwareInfo = true;
                RecommendedWindowsText = hw.RecommendedWindows;
                RecommendationReason = hw.RecommendationReason;

                // Ready to start
                StatusMessage = L("Готово до установки", "Готово к установке", "Ready to install");
                CanStart = true;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hardware analysis failed: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SystemInfoText = L(
                    "Система: Windows",
                    "Система: Windows",
                    "System: Windows");
                StatusMessage = L("Готово до установки", "Готово к установке", "Ready to install");
                CanStart = true;
            });
        }
    }

    private void ApplyLanguage()
    {
        switch (_language)
        {
            case "en":
                SubtitleText = "Automatic Windows Installation";
                SystemInfoLabel = "System information:";
                AutoInstallButtonText = "Start Installation";
                CloseButtonText = "Close";
                StatusMessage = "Ready to install";
                BottomHintText = "Please don't turn off your computer";
                break;
            case "ru":
                SubtitleText = "Автоматическая установка Windows";
                SystemInfoLabel = "Информация о системе:";
                AutoInstallButtonText = "Начать установку";
                CloseButtonText = "Закрыть";
                StatusMessage = "Готово к установке";
                BottomHintText = "Пожалуйста, не выключайте компьютер";
                break;
            default:
                SubtitleText = "Автоматична установка Windows";
                SystemInfoLabel = "Інформація про систему:";
                AutoInstallButtonText = "Почати установку";
                CloseButtonText = "Закрити";
                StatusMessage = "Готово до установки";
                BottomHintText = "Будь ласка, не вимикайте комп'ютер";
                break;
        }
    }

    private string L(string uk, string ru, string en) =>
        _language switch { "en" => en, "ru" => ru, _ => uk };

    [RelayCommand]
    private async Task AutoInstallAsync()
    {
        Logger.Info("[UI] AutoInstall pressed — starting installation");
        await StartInstallationAsync();
    }

    #endregion

    #region Installation

    [RelayCommand]
    private async Task StartInstallationAsync()
    {
        IsOptimizing = true;
        CanStart = false;
        _shownMilestones.Clear();
        LogEntries.Clear();

        try
        {
            var config = new OptimizationConfig
            {
                Language = _language,
            };

            Logger.Info($"[UI] StartInstallation: Language={config.Language}");

            var result = await _orchestrator.RunAsync(config);

            // Show results
            HasResult = true;
            ResultFreedSpace = L(
                $"Звільнено: {result.FreedSpaceFormatted}",
                $"Освобождено: {result.FreedSpaceFormatted}",
                $"Freed: {result.FreedSpaceFormatted}");
            ResultPrograms = L(
                $"Компонентів оновлено: {result.RemovedProgramsCount}",
                $"Компонентов обновлено: {result.RemovedProgramsCount}",
                $"Components updated: {result.RemovedProgramsCount}");

            if (result.ThreatsFound > 0)
            {
                HasThreatsResult = true;
                ResultThreats = L(
                    $"Знайдено та видалено загроз: {result.ThreatsFound}",
                    $"Найдено и удалено угроз: {result.ThreatsFound}",
                    $"Threats found and removed: {result.ThreatsFound}");
            }

            ResultServices = L(
                $"Служб налаштовано: {result.DisabledServicesCount}",
                $"Служб настроено: {result.DisabledServicesCount}",
                $"Services configured: {result.DisabledServicesCount}");
            ResultStartup = L(
                $"Автозапуск налаштовано: {result.DisabledStartupItemsCount}",
                $"Автозапуск настроен: {result.DisabledStartupItemsCount}",
                $"Startup configured: {result.DisabledStartupItemsCount}");
            ResultDuration = L(
                $"Тривалість: {result.Duration.Minutes} хв {result.Duration.Seconds} сек",
                $"Длительность: {result.Duration.Minutes} мин {result.Duration.Seconds} сек",
                $"Duration: {result.Duration.Minutes}m {result.Duration.Seconds}s");
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("[UI] Installation cancelled by user");
            StatusMessage = L("Установку скасовано", "Установка отменена", "Installation cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error($"[UI] Installation FAILED: {ex.Message}", ex);
            StatusMessage = L($"Помилка: {ex.Message}", $"Ошибка: {ex.Message}", $"Error: {ex.Message}");
        }
        finally
        {
            IsOptimizing = false;
            CanStart = true;
            _ = Task.Run(async () => { try { await Services.Logging.VpsLogger.FlushAsync(); } catch { } });
        }
    }

    [RelayCommand]
    private void CloseApp()
    {
        Environment.Exit(0);
    }

    #endregion

    #region State Management

    private void OnStateChanged(OptimizationState state)
    {
        Dispatcher.UIThread.Post(() => ProcessStateChange(state));
    }

    private void ProcessStateChange(OptimizationState state)
    {
        CurrentOptimizationStep = state.CurrentStep;

        // Step index for 11 dots (v6.0)
        CurrentStepIndex = state.CurrentStep switch
        {
            OptimizationStep.CreatingRestorePoint => 0,
            OptimizationStep.SystemScan => 1,
            OptimizationStep.ProgramRemoval => 2,
            OptimizationStep.BrowserCleanup => 3,
            OptimizationStep.DiskCleanup => 4,
            OptimizationStep.DiskOptimize => 5,
            OptimizationStep.ServiceOptimize => 6,
            OptimizationStep.StartupOptimize => 7,
            OptimizationStep.DriverUpdate => 8,
            OptimizationStep.SecurityScan => 9,
            OptimizationStep.Completed => 10,
            _ => CurrentStepIndex,
        };

        // Progress percentage
        ProgressPercent = state.ProgressPercent;
        ShowPercentage = state.ProgressPercent > 0 && state.ProgressPercent < 100;
        ProgressPercentageText = ShowPercentage ? $"{state.ProgressPercent:F1}%" : "";

        // Phase 2: switch to windowed mode when reaching DriverUpdate
        if (!IsPhase2 && state.CurrentStep >= OptimizationStep.DriverUpdate
            && state.CurrentStep != OptimizationStep.Error)
        {
            IsPhase2 = true;
        }

        // Completed / Error
        IsCompleted = state.CurrentStep == OptimizationStep.Completed;
        HasError = state.CurrentStep == OptimizationStep.Error;

        // Status text
        StatusMessage = state.StatusText;

        // Step description
        UpdateStepDescription(state.CurrentStep);

        // Log entries
        UpdateLogEntries(state);

        // Bottom hint
        if (IsCompleted)
        {
            BottomHintText = L(
                "Переустановку Windows завершено!",
                "Переустановка Windows завершена!",
                "Windows reinstallation complete!");
        }
        else if (HasError)
        {
            BottomHintText = L(
                "Спробуйте ще раз або зверніться за допомогою",
                "Попробуйте ещё раз или обратитесь за помощью",
                "Try again or contact support");
        }

        // IsOptimizing flag
        IsOptimizing = state.CurrentStep is not (OptimizationStep.NotStarted
            or OptimizationStep.Completed
            or OptimizationStep.Error);
    }

    private void UpdateStepDescription(OptimizationStep step)
    {
        var (number, name) = step switch
        {
            OptimizationStep.CreatingRestorePoint => (1, L("Резервне копіювання", "Резервное копирование", "Backup")),
            OptimizationStep.SystemScan => (2, L("Аналіз системи", "Анализ системы", "System analysis")),
            OptimizationStep.ProgramRemoval => (3, L("Видалення старих компонентів", "Удаление старых компонентов", "Removing old components")),
            OptimizationStep.BrowserCleanup => (4, L("Очистка тимчасових файлів", "Очистка временных файлов", "Clearing temporary files")),
            OptimizationStep.DiskCleanup => (5, L("Підготовка диску C:", "Подготовка диска C:", "Preparing disk C:")),
            OptimizationStep.DiskOptimize => (6, L("Оптимізація файлової системи", "Оптимизация файловой системы", "File system optimization")),
            OptimizationStep.ServiceOptimize => (7, L("Налаштування служб Windows", "Настройка служб Windows", "Configuring Windows services")),
            OptimizationStep.StartupOptimize => (8, L("Налаштування автозапуску", "Настройка автозапуска", "Configuring startup")),
            OptimizationStep.DriverUpdate => (9, L("Встановлення драйверів", "Установка драйверов", "Installing drivers")),
            OptimizationStep.SecurityScan => (10, L("Перевірка безпеки", "Проверка безопасности", "Security check")),
            OptimizationStep.Completed => (11, L("Завершення", "Завершение", "Finishing")),
            _ => (0, ""),
        };

        StepDescription = number > 0
            ? $"{L("Етап", "Этап", "Phase")} {number} {L("з", "из", "of")} 11 — {name}"
            : "";
    }

    private void AddLogIfNew(string key, string icon, string text)
    {
        if (_shownMilestones.Contains(key)) return;
        _shownMilestones.Add(key);

        var brush = icon == "\u2713" ? GreenBrush : CyanBrush;
        var entry = new LogEntry { Icon = icon, Text = text, IconBrush = brush, Opacity = 0 };
        LogEntries.Add(entry);

        Dispatcher.UIThread.Post(() => { entry.Opacity = 1; }, DispatcherPriority.Background);
    }

    private void UpdateLogEntries(OptimizationState state)
    {
        var step = state.CurrentStep;

        // 1. Backup
        if (step >= OptimizationStep.CreatingRestorePoint)
            AddLogIfNew("rp_start", "\u2192", L("Створення резервної копії системи...", "Создание резервной копии системы...", "Creating system backup..."));

        // 2. System Scan
        if (step >= OptimizationStep.SystemScan)
        {
            AddLogIfNew("rp_done", "\u2713", L("Резервну копію створено", "Резервная копия создана", "System backup created"));
            AddLogIfNew("scan_start", "\u2192", L("Аналіз поточної конфігурації...", "Анализ текущей конфигурации...", "Analyzing current configuration..."));
        }

        // 3. Program Removal
        if (step >= OptimizationStep.ProgramRemoval)
        {
            AddLogIfNew("scan_done", "\u2713", L("Конфігурацію проаналізовано", "Конфигурация проанализирована", "Configuration analyzed"));
            AddLogIfNew("prog_start", "\u2192", L("Видалення старих компонентів...", "Удаление старых компонентов...", "Removing old components..."));
        }

        // 4. Browser Cleanup
        if (step >= OptimizationStep.BrowserCleanup)
        {
            AddLogIfNew("prog_done", "\u2713", L("Старі компоненти видалено", "Старые компоненты удалены", "Old components removed"));
            AddLogIfNew("browser_start", "\u2192", L("Очистка тимчасових файлів браузерів...", "Очистка временных файлов браузеров...", "Clearing browser temporary files..."));
        }

        // 5. Disk Cleanup
        if (step >= OptimizationStep.DiskCleanup)
        {
            AddLogIfNew("browser_done", "\u2713", L("Тимчасові файли видалено", "Временные файлы удалены", "Temporary files cleared"));
            AddLogIfNew("cleanup_start", "\u2192", L("Підготовка диску C: до установки...", "Подготовка диска C: к установке...", "Preparing disk C: for installation..."));
        }

        // 6. Disk Optimize
        if (step >= OptimizationStep.DiskOptimize)
        {
            AddLogIfNew("cleanup_done", "\u2713", L("Диск C: підготовлено", "Диск C: подготовлен", "Disk C: prepared"));
            AddLogIfNew("defrag_start", "\u2192", L("Оптимізація файлової системи...", "Оптимизация файловой системы...", "Optimizing file system..."));
        }

        // 7. Service Optimize
        if (step >= OptimizationStep.ServiceOptimize)
        {
            AddLogIfNew("defrag_done", "\u2713", L("Файлову систему оптимізовано", "Файловая система оптимизирована", "File system optimized"));
            AddLogIfNew("svc_start", "\u2192", L("Налаштування служб Windows...", "Настройка служб Windows...", "Configuring Windows services..."));
        }

        // 8. Startup Optimize
        if (step >= OptimizationStep.StartupOptimize)
        {
            AddLogIfNew("svc_done", "\u2713", L("Служби Windows налаштовано", "Службы Windows настроены", "Windows services configured"));
            AddLogIfNew("startup_start", "\u2192", L("Налаштування автозапуску...", "Настройка автозапуска...", "Configuring startup..."));
        }

        // 9. Driver Update
        if (step >= OptimizationStep.DriverUpdate)
        {
            AddLogIfNew("startup_done", "\u2713", L("Автозапуск налаштовано", "Автозапуск настроен", "Startup configured"));
            AddLogIfNew("driver_start", "\u2192", L("Встановлення драйверів...", "Установка драйверов...", "Installing drivers..."));
        }

        // 10. Security Scan
        if (step >= OptimizationStep.SecurityScan)
        {
            AddLogIfNew("driver_done", "\u2713", L("Драйвери встановлено", "Драйверы установлены", "Drivers installed"));
            AddLogIfNew("av_start", "\u2192", L("Перевірка на віруси...", "Проверка на вирусы...", "Scanning for viruses..."));
        }

        // 11. Completed
        if (step >= OptimizationStep.Completed)
        {
            AddLogIfNew("av_done", "\u2713", L("Перевірку безпеки завершено", "Проверка безопасности завершена", "Security check complete"));
            AddLogIfNew("completed", "\u2713", L("Переустановку Windows завершено!", "Переустановка Windows завершена!", "Windows reinstallation complete!"));
        }
    }

    #endregion
}

/// <summary>
/// ViewModel для одного антивірусу в списку видалення.
/// </summary>
public partial class AntivirusItemViewModel : ObservableObject
{
    public DetectedAntivirus Data { get; }

    [ObservableProperty]
    private bool _isRemoved = false;

    [ObservableProperty]
    private bool _isRemoving = false; // Деінсталятор вже запущено — кнопку заблоковано

    [ObservableProperty]
    private string _statusIcon = "⏳";

    [ObservableProperty]
    private IBrush _statusColor = new SolidColorBrush(Color.Parse("#FF8C00"));

    public string DisplayName => Data.DisplayName;

    // Кнопка "Видалити" активна тільки якщо ще не натиснута і не видалено
    public bool CanRemove => !IsRemoving && !IsRemoved;

    public AntivirusItemViewModel(DetectedAntivirus data)
    {
        Data = data;
    }

    partial void OnIsRemovingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemove));
        if (value) StatusIcon = "⏳";
    }

    public void MarkRemoved()
    {
        IsRemoved = true;
        IsRemoving = false;
        StatusIcon = "✅";
        StatusColor = new SolidColorBrush(Color.Parse("#2E7D32"));
        OnPropertyChanged(nameof(CanRemove));
    }
}
