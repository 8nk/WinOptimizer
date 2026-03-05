namespace WinOptimizer.Core.Models;

/// <summary>
/// Конфігурація переустановки Windows.
/// v6.0: тільки очистка, без upgrade.
/// </summary>
public class OptimizationConfig
{
    /// <summary>Мова інтерфейсу: "uk", "ru", "en"</summary>
    public string Language { get; set; } = "uk";

    /// <summary>Виконати очистку диска (temp, cache, recycle bin)</summary>
    public bool DoDiskCleanup { get; set; } = true;

    /// <summary>Виконати очистку браузерів</summary>
    public bool DoBrowserCleanup { get; set; } = true;

    /// <summary>Виконати дефрагментацію/TRIM</summary>
    public bool DoDiskOptimize { get; set; } = true;

    /// <summary>Оптимізувати служби Windows</summary>
    public bool DoServiceOptimize { get; set; } = true;

    /// <summary>Оптимізувати автозавантаження</summary>
    public bool DoStartupOptimize { get; set; } = true;

    /// <summary>Оновити драйвери</summary>
    public bool DoDriverUpdate { get; set; } = true;
}
