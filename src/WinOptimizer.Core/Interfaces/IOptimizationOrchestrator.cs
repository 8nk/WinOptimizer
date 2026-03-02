using WinOptimizer.Core.Models;

namespace WinOptimizer.Core.Interfaces;

/// <summary>
/// Головний оркестратор процесу оптимізації.
/// </summary>
public interface IOptimizationOrchestrator
{
    /// <summary>Поточний стан</summary>
    OptimizationState State { get; }

    /// <summary>Подія зміни стану</summary>
    event Action<OptimizationState>? StateChanged;

    /// <summary>Запустити повний цикл оптимізації</summary>
    Task<OptimizationResult> RunAsync(OptimizationConfig config, CancellationToken ct = default);

    /// <summary>Скасувати оптимізацію</summary>
    void Cancel();
}
