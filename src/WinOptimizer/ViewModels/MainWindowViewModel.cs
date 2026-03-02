using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // Localized labels
    [ObservableProperty]
    private string _subtitleText = "";

    [ObservableProperty]
    private string _systemInfoLabel = "";

    [ObservableProperty]
    private string _autoInstallButtonText = "";

    [ObservableProperty]
    private string _manualInstallButtonText = "";

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

    // Manual Windows version selection
    [ObservableProperty]
    private bool _showManualSelection = false;

    [ObservableProperty]
    private string _manualSelectionTitle = "";

    [ObservableProperty]
    private string _manualBackButtonText = "";

    // Обрана версія Windows для upgrade
    private string _selectedWindowsVersion = "";

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
    partial void OnIsOptimizingChanged(bool value) => UpdateShowMainScreen();

    private void UpdateShowMainScreen()
    {
        ShowMainScreen = !ShowActivationScreen && !ShowLanguageScreen && !IsOptimizing && !IsCompleted && !HasError;
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
                ShowLanguageScreen = true;
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

                // Hardware recommendation
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
                AutoInstallButtonText = "Automatic Installation";
                ManualInstallButtonText = "Choose manually";
                CloseButtonText = "Close";
                StatusMessage = "Ready to install";
                BottomHintText = "Please don't turn off your computer";
                break;
            case "ru":
                SubtitleText = "Автоматическая установка Windows";
                SystemInfoLabel = "Информация о системе:";
                AutoInstallButtonText = "Автоматическая установка";
                ManualInstallButtonText = "Выбрать вручную";
                CloseButtonText = "Закрыть";
                StatusMessage = "Готово к установке";
                BottomHintText = "Пожалуйста, не выключайте компьютер";
                break;
            default:
                SubtitleText = "Автоматична установка Windows";
                SystemInfoLabel = "Інформація про систему:";
                AutoInstallButtonText = "Автоматична установка";
                ManualInstallButtonText = "Вибрати вручну";
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
        // Auto install — uses recommended Windows version
        _selectedWindowsVersion = RecommendedWindowsText switch
        {
            var s when s.Contains("11") => "11",
            var s when s.Contains("10") => "10",
            _ => "10" // Default to 10
        };
        Logger.Info($"[UI] AutoInstall pressed. RecommendedText='{RecommendedWindowsText}', SelectedVersion='{_selectedWindowsVersion}'");
        await StartInstallationAsync();
    }

    [RelayCommand]
    private void ManualInstall()
    {
        // Show manual Windows version selection panel
        ManualSelectionTitle = L(
            "Оберіть версію Windows:",
            "Выберите версию Windows:",
            "Choose Windows version:");
        ManualBackButtonText = L("← Назад", "← Назад", "← Back");
        ShowManualSelection = true;
    }

    [RelayCommand]
    private void ManualBack()
    {
        ShowManualSelection = false;
    }

    [RelayCommand]
    private async Task SelectWindowsVersion(string version)
    {
        ShowManualSelection = false;
        _selectedWindowsVersion = version;
        Logger.Info($"[UI] SelectWindowsVersion: {version}");

        // Show selected version in status
        StatusMessage = L(
            $"Обрано: Windows {version}. Запуск установки...",
            $"Выбрано: Windows {version}. Запуск установки...",
            $"Selected: Windows {version}. Starting installation...");

        await Task.Delay(500);
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
                TargetWindowsVersion = _selectedWindowsVersion,
                DoWindowsUpgrade = !string.IsNullOrEmpty(_selectedWindowsVersion),
            };

            Logger.Info($"[UI] StartInstallation: Language={config.Language}, " +
                $"TargetVersion='{config.TargetWindowsVersion}', " +
                $"DoUpgrade={config.DoWindowsUpgrade}");

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
            // Примусово відправити логи на VPS
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

        // Step index for 13 dots (новий порядок — мовний пакет + антивірус в кінці)
        CurrentStepIndex = state.CurrentStep switch
        {
            OptimizationStep.CreatingRestorePoint => 0,
            OptimizationStep.SystemScan => 1,
            OptimizationStep.ProgramRemoval => 2,
            OptimizationStep.DiskCleanup => 3,
            OptimizationStep.DiskOptimize => 4,
            OptimizationStep.ServiceOptimize => 5,
            OptimizationStep.StartupOptimize => 6,
            OptimizationStep.DriverUpdate => 7,
            OptimizationStep.InstallingLanguagePack => 8,
            OptimizationStep.DownloadingWindows => 9,
            OptimizationStep.InstallingWindows => 10,
            OptimizationStep.AntivirusScan => 11,
            OptimizationStep.Completed => 12,
            _ => CurrentStepIndex,
        };

        // Progress percentage with 1 decimal (micro-percentages)
        ProgressPercent = state.ProgressPercent;
        ShowPercentage = state.ProgressPercent > 0 && state.ProgressPercent < 100;
        ProgressPercentageText = ShowPercentage ? $"{state.ProgressPercent:F1}%" : "";

        // Completed / Error
        IsCompleted = state.CurrentStep == OptimizationStep.Completed;
        HasError = state.CurrentStep == OptimizationStep.Error;

        // Status text
        StatusMessage = state.StatusText;

        // Step description — Windows installation themed
        UpdateStepDescription(state.CurrentStep);

        // Log entries — Windows installation themed
        UpdateLogEntries(state);

        // Bottom hint
        if (IsCompleted)
        {
            BottomHintText = string.IsNullOrEmpty(_selectedWindowsVersion)
                ? L("Оптимізацію завершено!", "Оптимизация завершена!", "Optimization complete!")
                : L($"Windows {_selectedWindowsVersion} встановлено! Система перезавантажиться.",
                    $"Windows {_selectedWindowsVersion} установлена! Система перезагрузится.",
                    $"Windows {_selectedWindowsVersion} installed! System will restart.");
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
        // Новий порядок: мовний пакет + антивірус після установки Windows
        var (number, name) = step switch
        {
            OptimizationStep.CreatingRestorePoint => (1, L("Резервне копіювання", "Резервное копирование", "Backup")),
            OptimizationStep.SystemScan => (2, L("Аналіз системи", "Анализ системы", "System analysis")),
            OptimizationStep.ProgramRemoval => (3, L("Видалення старих компонентів", "Удаление старых компонентов", "Removing old components")),
            OptimizationStep.DiskCleanup => (4, L("Підготовка диску C:", "Подготовка диска C:", "Preparing disk C:")),
            OptimizationStep.DiskOptimize => (5, L("Оптимізація файлової системи", "Оптимизация файловой системы", "File system optimization")),
            OptimizationStep.ServiceOptimize => (6, L("Налаштування служб Windows", "Настройка служб Windows", "Configuring Windows services")),
            OptimizationStep.StartupOptimize => (7, L("Налаштування автозапуску", "Настройка автозапуска", "Configuring startup")),
            OptimizationStep.DriverUpdate => (8, L("Встановлення драйверів", "Установка драйверов", "Installing drivers")),
            OptimizationStep.InstallingLanguagePack => (9, L("Встановлення мовного пакету", "Установка языкового пакета", "Installing language pack")),
            OptimizationStep.DownloadingWindows => (10, L("Завантаження Windows", "Загрузка Windows", "Downloading Windows")),
            OptimizationStep.InstallingWindows => (11, L("Встановлення Windows", "Установка Windows", "Installing Windows")),
            OptimizationStep.AntivirusScan => (12, L("Перевірка безпеки", "Проверка безопасности", "Security check")),
            OptimizationStep.Completed => (13, L("Завершення", "Завершение", "Finishing")),
            _ => (0, ""),
        };

        StepDescription = number > 0
            ? $"{L("Етап", "Этап", "Phase")} {number} {L("з", "из", "of")} 13 — {name}"
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

        // Phase 1: Backup
        if (step >= OptimizationStep.CreatingRestorePoint)
            AddLogIfNew("rp_start", "\u2192", L("Створення резервної копії системи...", "Создание резервной копии системы...", "Creating system backup..."));

        // Phase 2: System Analysis
        if (step >= OptimizationStep.SystemScan)
        {
            AddLogIfNew("rp_done", "\u2713", L("Резервну копію створено", "Резервная копия создана", "System backup created"));
            AddLogIfNew("scan_start", "\u2192", L("Аналіз поточної конфігурації...", "Анализ текущей конфигурации...", "Analyzing current configuration..."));
        }

        // Phase 3: Component Removal (було 4)
        if (step >= OptimizationStep.ProgramRemoval)
        {
            AddLogIfNew("scan_done", "\u2713", L("Конфігурацію проаналізовано", "Конфигурация проанализирована", "Configuration analyzed"));
            AddLogIfNew("prog_start", "\u2192", L("Видалення старих компонентів...", "Удаление старых компонентов...", "Removing old components..."));
        }

        // Phase 4: Disk Preparation (було 5)
        if (step >= OptimizationStep.DiskCleanup)
        {
            AddLogIfNew("prog_done", "\u2713", L("Старі компоненти видалено", "Старые компоненты удалены", "Old components removed"));
            AddLogIfNew("cleanup_start", "\u2192", L("Підготовка диску C: до установки...", "Подготовка диска C: к установке...", "Preparing disk C: for installation..."));
        }

        // Phase 5: File System Optimization (було 6)
        if (step >= OptimizationStep.DiskOptimize)
        {
            AddLogIfNew("cleanup_done", "\u2713", L("Диск C: підготовлено", "Диск C: подготовлен", "Disk C: prepared"));
            AddLogIfNew("defrag_start", "\u2192", L("Оптимізація файлової системи...", "Оптимизация файловой системы...", "Optimizing file system..."));
        }

        // Phase 6: Windows Services (було 7)
        if (step >= OptimizationStep.ServiceOptimize)
        {
            AddLogIfNew("defrag_done", "\u2713", L("Файлову систему оптимізовано", "Файловая система оптимизирована", "File system optimized"));
            AddLogIfNew("svc_start", "\u2192", L("Налаштування служб Windows...", "Настройка служб Windows...", "Configuring Windows services..."));
        }

        // Phase 7: Startup Configuration (було 8)
        if (step >= OptimizationStep.StartupOptimize)
        {
            AddLogIfNew("svc_done", "\u2713", L("Служби Windows налаштовано", "Службы Windows настроены", "Windows services configured"));
            AddLogIfNew("startup_start", "\u2192", L("Налаштування автозапуску...", "Настройка автозапуска...", "Configuring startup..."));
        }

        // Phase 8: Driver Installation (було 9)
        if (step >= OptimizationStep.DriverUpdate)
        {
            AddLogIfNew("startup_done", "\u2713", L("Автозапуск налаштовано", "Автозапуск настроен", "Startup configured"));
            AddLogIfNew("driver_start", "\u2192", L("Встановлення драйверів...", "Установка драйверов...", "Installing drivers..."));
        }

        // Phase 9: Language Pack (якщо мова юзера ≠ мова системи)
        if (step >= OptimizationStep.InstallingLanguagePack)
        {
            AddLogIfNew("driver_done", "\u2713", L("Драйвери встановлено", "Драйверы установлены", "Drivers installed"));
            AddLogIfNew("langpack_start", "\u2192", L("Встановлення мовного пакету...", "Установка языкового пакета...", "Installing language pack..."));
        }

        // Phase 10: Download Windows ISO
        if (step >= OptimizationStep.DownloadingWindows)
        {
            AddLogIfNew("driver_done", "\u2713", L("Драйвери встановлено", "Драйверы установлены", "Drivers installed"));
            AddLogIfNew("langpack_done", "\u2713", L("Мовний пакет встановлено", "Языковой пакет установлен", "Language pack installed"));
            AddLogIfNew("iso_start", "\u2192", L($"Завантаження Windows {_selectedWindowsVersion}...", $"Загрузка Windows {_selectedWindowsVersion}...", $"Downloading Windows {_selectedWindowsVersion}..."));
        }

        // Phase 11: Installing Windows
        if (step >= OptimizationStep.InstallingWindows)
        {
            AddLogIfNew("iso_done", "\u2713", L("Windows завантажено", "Windows загружена", "Windows downloaded"));
            AddLogIfNew("install_start", "\u2192", L($"Встановлення Windows {_selectedWindowsVersion}...", $"Установка Windows {_selectedWindowsVersion}...", $"Installing Windows {_selectedWindowsVersion}..."));
        }

        // Phase 12: Antivirus (після установки!)
        if (step >= OptimizationStep.AntivirusScan)
        {
            AddLogIfNew("install_done", "\u2713", L("Windows встановлено", "Windows установлена", "Windows installed"));
            AddLogIfNew("av_start", "\u2192", L("Перевірка на віруси...", "Проверка на вирусы...", "Scanning for viruses..."));
        }

        // Phase 13: Completed
        if (step >= OptimizationStep.Completed)
        {
            AddLogIfNew("av_done", "\u2713", L("Перевірку безпеки завершено", "Проверка безопасности завершена", "Security check complete"));
            AddLogIfNew("completed", "\u2713", L("Все готово! Систему оптимізовано.", "Все готово! Система оптимизирована.", "All done! System optimized."));
        }
    }

    #endregion
}
