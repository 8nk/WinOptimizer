namespace WinOptimizer.Core.Models;

/// <summary>
/// Результат оптимізації — before/after статистика.
/// </summary>
public class OptimizationResult
{
    /// <summary>Звільнено простору (bytes)</summary>
    public long FreedSpace { get; set; }

    /// <summary>Вимкнено служб</summary>
    public int DisabledServicesCount { get; set; }

    /// <summary>Видалено startup items</summary>
    public int DisabledStartupItemsCount { get; set; }

    /// <summary>Чи виконано defrag/trim</summary>
    public bool DefragPerformed { get; set; }

    /// <summary>Видалено програм</summary>
    public int RemovedProgramsCount { get; set; }

    /// <summary>Знайдено загроз антивірусом</summary>
    public int ThreatsFound { get; set; }

    /// <summary>Чи оновлено драйвери</summary>
    public bool DriversUpdated { get; set; }

    /// <summary>Кількість debloat оптимізацій (registry tweaks)</summary>
    public int DebloatTweaksCount { get; set; }

    /// <summary>Вільне місце до</summary>
    public long FreeSpaceBefore { get; set; }

    /// <summary>Вільне місце після</summary>
    public long FreeSpaceAfter { get; set; }

    /// <summary>Тривалість оптимізації</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Форматований звільнений простір</summary>
    public string FreedSpaceFormatted => SystemScanResult.FormatSize(FreedSpace);
}
