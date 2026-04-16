namespace WinOptimizer.Core.Models;

/// <summary>
/// Результат пре-сканування системи.
/// </summary>
public class SystemScanResult
{
    /// <summary>Загальний розмір тимчасових файлів (bytes)</summary>
    public long TempFilesSize { get; set; }

    /// <summary>Розмір кешу браузерів (bytes)</summary>
    public long BrowserCacheSize { get; set; }

    /// <summary>Розмір кошика (bytes)</summary>
    public long RecycleBinSize { get; set; }

    /// <summary>Розмір Windows логів (bytes)</summary>
    public long WindowsLogsSize { get; set; }

    /// <summary>Кількість служб що можна вимкнути</summary>
    public int DisableableServicesCount { get; set; }

    /// <summary>Кількість startup items</summary>
    public int StartupItemsCount { get; set; }

    /// <summary>Чи є SSD (для вибору defrag/trim)</summary>
    public bool IsSsd { get; set; }

    /// <summary>Вільне місце на C: до оптимізації (bytes)</summary>
    public long FreeSpaceBefore { get; set; }

    /// <summary>Загальний розмір C: (bytes)</summary>
    public long TotalDiskSize { get; set; }

    /// <summary>Загальний потенційний виграш (bytes)</summary>
    public long TotalCleanableSize => TempFilesSize + BrowserCacheSize + RecycleBinSize + WindowsLogsSize;

    /// <summary>Форматований розмір</summary>
    public string TotalCleanableSizeFormatted => FormatSize(TotalCleanableSize);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
