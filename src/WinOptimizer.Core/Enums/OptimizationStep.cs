namespace WinOptimizer.Core.Enums;

/// <summary>
/// Кроки переустановки Windows — 11 кроків.
/// v6.0: видалено upgrade (ISO/langpack), реальна очистка + rollback.
/// </summary>
public enum OptimizationStep
{
    NotStarted,
    CreatingRestorePoint,   // 1
    SystemScan,             // 2
    ProgramRemoval,         // 3
    BrowserCleanup,         // 4
    DiskCleanup,            // 5
    DiskOptimize,           // 6
    ServiceOptimize,        // 7
    StartupOptimize,        // 8
    DriverUpdate,           // 9
    SecurityScan,           // 10
    Completed,              // 11
    Error
}
