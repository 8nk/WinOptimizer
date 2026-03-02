namespace WinOptimizer.Core.Enums;

/// <summary>
/// Кроки оптимізації + установки Windows — 12 кроків.
/// Антивірус перенесено в кінець (після установки Windows).
/// </summary>
public enum OptimizationStep
{
    NotStarted,
    CreatingRestorePoint,   // 1
    SystemScan,             // 2
    ProgramRemoval,         // 3 (було 4)
    DiskCleanup,            // 4 (було 5)
    DiskOptimize,           // 5 (було 6)
    ServiceOptimize,        // 6 (було 7)
    StartupOptimize,        // 7 (було 8)
    DriverUpdate,           // 8 (було 9)
    DownloadingWindows,     // 9 (було 10)
    InstallingWindows,      // 10 (було 11)
    AntivirusScan,          // 11 (було 3, тепер в кінці!)
    Completed,              // 12
    Error
}
