using WinOptimizer.Core.Enums;

namespace WinOptimizer.Core.Models;

/// <summary>
/// Поточний стан процесу оптимізації.
/// </summary>
public class OptimizationState
{
    public OptimizationStep CurrentStep { get; set; } = OptimizationStep.NotStarted;
    public string StatusText { get; set; } = "";
    public string DetailText { get; set; } = "";
    public double ProgressPercent { get; set; }
    public bool IsRunning { get; set; }
    public bool IsCompleted { get; set; }
    public bool HasError { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Час початку оптимізації</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Час завершення</summary>
    public DateTime? CompletedAt { get; set; }
}
