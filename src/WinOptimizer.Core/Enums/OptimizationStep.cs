namespace WinOptimizer.Core.Enums;

/// <summary>
/// Кроки оптимізації + установки Windows — 13 кроків.
/// Мовний пакет додано перед завантаженням ISO.
/// Антивірус в кінці (після установки Windows).
/// </summary>
public enum OptimizationStep
{
    NotStarted,
    CreatingRestorePoint,   // 1
    SystemScan,             // 2
    ProgramRemoval,         // 3
    DiskCleanup,            // 4
    DiskOptimize,           // 5
    ServiceOptimize,        // 6
    StartupOptimize,        // 7
    DriverUpdate,           // 8
    InstallingLanguagePack, // 9  — мовний пакет (якщо мова юзера ≠ мова системи)
    DownloadingWindows,     // 10
    InstallingWindows,      // 11
    AntivirusScan,          // 12
    Completed,              // 13
    Error
}
