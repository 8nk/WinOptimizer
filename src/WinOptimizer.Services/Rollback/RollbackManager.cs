using WinOptimizer.Core.Models;
using WinOptimizer.Services.Logging;
using WinOptimizer.Services.Optimization;

namespace WinOptimizer.Services.Rollback;

/// <summary>
/// Менеджер відкату — System Restore Point (пріоритет) або ручне відновлення.
/// </summary>
public static class RollbackManager
{
    public static void SaveState(RollbackState state)
    {
        try
        {
            state.Save();
            Logger.Info($"Rollback state saved: RP#{state.RestorePointSequenceNumber}, " +
                        $"{state.DisabledServices.Count} services, {state.DisabledStartupItems.Count} startup, " +
                        $"{state.RemovedPrograms.Count} programs");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save rollback state", ex);
        }
    }

    /// <summary>
    /// Виконати відкат:
    /// 1. Якщо є System Restore Point → відновити (reboot)
    /// 2. Fallback → ручне відновлення служб/startup
    /// </summary>
    public static async Task<bool> ExecuteRollbackAsync(Action<string>? onProgress = null)
    {
        try
        {
            var state = RollbackState.Load();
            if (state == null)
            {
                Logger.Warn("No rollback state found");
                onProgress?.Invoke("Стан відкату не знайдено");
                return false;
            }

            // Priority 1: System Restore Point
            if (state.HasRestorePoint)
            {
                Logger.Info($"Initiating System Restore to point #{state.RestorePointSequenceNumber}");
                onProgress?.Invoke("Відновлення системи до точки збереження...");

                try
                {
                    await SystemRestoreService.InitiateRestoreAsync(state.RestorePointSequenceNumber);
                    RollbackState.Delete();
                    Logger.Info("System Restore initiated, reboot scheduled");
                    onProgress?.Invoke("Система буде відновлена після перезавантаження (15 сек)");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"System Restore failed, falling back to manual: {ex.Message}");
                    onProgress?.Invoke("System Restore недоступний, ручне відновлення...");
                }
            }

            // Priority 2: Manual rollback (services + startup)
            Logger.Info($"Manual rollback: {state.DisabledServices.Count} services, {state.DisabledStartupItems.Count} startup items");

            foreach (var svc in state.DisabledServices)
            {
                onProgress?.Invoke($"Відновлення служби: {svc.DisplayName}...");
                await Task.Run(() => ServiceOptimizer.RestoreService(svc));
            }

            foreach (var item in state.DisabledStartupItems)
            {
                onProgress?.Invoke($"Відновлення: {item.ValueName}...");
                await Task.Run(() => StartupOptimizer.RestoreStartupItem(item));
            }

            onProgress?.Invoke("Очистка...");
            RollbackState.Delete();

            Logger.Info("Manual rollback completed");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Rollback failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Cleanup після оплати — видалити restore point + state + agent
    /// </summary>
    public static void Cleanup()
    {
        try
        {
            var state = RollbackState.Load();

            // Delete restore point if exists
            if (state?.HasRestorePoint == true)
            {
                try
                {
                    SystemRestoreService.DeleteRestorePointAsync(state.RestorePointSequenceNumber)
                        .GetAwaiter().GetResult();
                    Logger.Info($"Restore point #{state.RestorePointSequenceNumber} deleted");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to delete restore point: {ex.Message}");
                }
            }

            RollbackState.Delete();

            // Delete scheduled task
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Delete /TN \"WinOptimizerAgent\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
            }
            catch { }

            // Delete agent
            var agentDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WinOptimizer", "Agent");
            try
            {
                if (Directory.Exists(agentDir))
                    Directory.Delete(agentDir, true);
            }
            catch { }

            Logger.Info("Rollback cleanup completed (payment)");
        }
        catch (Exception ex)
        {
            Logger.Error("Cleanup failed", ex);
        }
    }
}
