using System.Diagnostics;
using System.Reflection;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Rollback;

/// <summary>
/// Розгортання WinOptimizerAgent — витягує з ресурсів, створює scheduled task.
/// Агент запускається після оптимізації і чекає команд з VPS (rollback/payment).
/// Логи записуються на Public Desktop для діагностики.
/// </summary>
public static class AgentDeployer
{
    private static readonly string AgentDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Agent");

    private static readonly string AgentExePath = Path.Combine(AgentDir, "WinOptimizerAgent.exe");
    private const string TaskName = "WinOptimizerAgent";

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Logs");
    private static readonly string DesktopLog = Path.Combine(LogDir, "deploy.log");

    private static void DLog(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [DEPLOY] {msg}";
        Logger.Info($"[DEPLOY] {msg}");
        try { Directory.CreateDirectory(LogDir); File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
    }

    /// <summary>
    /// Розгорнути агент: витягти EXE з ресурсів + створити scheduled task.
    /// </summary>
    public static async Task<bool> DeployAsync(Action<string>? onProgress = null)
    {
        try
        {
            DLog("========== AGENT DEPLOY START ==========");
            DLog($"AgentDir: {AgentDir}");
            DLog($"AgentExe: {AgentExePath}");
            DLog($"DesktopLog: {DesktopLog}");
            onProgress?.Invoke("Встановлення агента...");

            // 0. Kill old agent — MUST stop scheduled task + kill process (runs as SYSTEM!)
            try
            {
                // Stop scheduled task first (graceful)
                await RunPsAsync($"Stop-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue");
                DLog("Stop scheduled task: done");

                // Force kill via taskkill (works for SYSTEM processes too when caller is admin)
                var killPsi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/F /IM WinOptimizerAgent.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var killProc = Process.Start(killPsi);
                if (killProc != null)
                {
                    var killOut = await killProc.StandardOutput.ReadToEndAsync();
                    var killErr = await killProc.StandardError.ReadToEndAsync();
                    await killProc.WaitForExitAsync();
                    DLog($"taskkill output: {killOut.Trim()} {killErr.Trim()}");
                }

                // Wait for file to be released
                await Task.Delay(2000);

                // Verify no process left
                var checkPs = await RunPsAsync(
                    "Get-Process WinOptimizerAgent -ErrorAction SilentlyContinue | Select-Object Id");
                DLog($"After kill check: {(string.IsNullOrWhiteSpace(checkPs) ? "CLEAN — no process" : checkPs.Trim())}");
            }
            catch (Exception ex)
            {
                DLog($"Kill old agent error: {ex.Message}");
            }

            // 1. Extract agent EXE from embedded resources
            var extracted = await ExtractAgentAsync();
            if (!extracted)
            {
                DLog("FAIL: could not extract agent EXE from resources");
                return false;
            }

            // 2. Create scheduled task (runs at logon, with highest privileges)
            var taskCreated = await CreateScheduledTaskAsync();
            DLog($"Scheduled task created: {taskCreated}");
            if (!taskCreated)
            {
                DLog("WARN: scheduled task failed, will try direct start");
            }

            // 3. Start agent immediately
            var started = await StartAgentAsync();
            DLog($"Agent started: {started}");

            // 4. Verify agent is running
            await Task.Delay(2000);
            var verifyOutput = await RunPsAsync(
                "Get-Process WinOptimizerAgent -ErrorAction SilentlyContinue | Select-Object Id,ProcessName | Format-List");
            DLog($"Agent process check: {(string.IsNullOrWhiteSpace(verifyOutput) ? "NOT FOUND!" : verifyOutput.Trim())}");

            // 5. Check scheduled task status
            var taskStatus = await RunPsAsync(
                $"Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue | Select-Object State,TaskName | Format-List");
            DLog($"Task status: {(string.IsNullOrWhiteSpace(taskStatus) ? "NOT FOUND!" : taskStatus.Trim())}");

            DLog("========== AGENT DEPLOY COMPLETE ==========");
            onProgress?.Invoke("Агент встановлено");
            return true;
        }
        catch (Exception ex)
        {
            DLog($"DEPLOY ERROR: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Витягти WinOptimizerAgent.exe з вбудованих ресурсів (EmbeddedResource).
    /// </summary>
    private static async Task<bool> ExtractAgentAsync()
    {
        try
        {
            Directory.CreateDirectory(AgentDir);
            DLog($"AgentDir created/exists: {Directory.Exists(AgentDir)}");

            // Try multiple assemblies
            var assembly = Assembly.GetEntryAssembly();
            DLog($"GetEntryAssembly: {assembly?.FullName ?? "NULL"}");

            if (assembly == null)
            {
                assembly = Assembly.GetExecutingAssembly();
                DLog($"GetExecutingAssembly fallback: {assembly?.FullName ?? "NULL"}");
            }

            if (assembly == null)
            {
                DLog("FAIL: no assembly found");
                return false;
            }

            // List ALL resources for debugging
            var resourceNames = assembly.GetManifestResourceNames();
            DLog($"Found {resourceNames.Length} embedded resources:");
            foreach (var name in resourceNames)
                DLog($"  -> {name} ({name.Length} chars)");

            // Find agent resource — try exact name first, then search
            Stream? stream = assembly.GetManifestResourceStream("WinOptimizerAgent.exe");
            DLog($"Exact name 'WinOptimizerAgent.exe': {(stream != null ? "FOUND" : "not found")}");

            if (stream == null)
            {
                // Fallback: search by partial name
                var match = resourceNames.FirstOrDefault(n =>
                    n.Contains("WinOptimizerAgent", StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    DLog($"Fallback match: '{match}'");
                    stream = assembly.GetManifestResourceStream(match);
                    DLog($"Fallback stream: {(stream != null ? "FOUND" : "not found")}");
                }
                else
                {
                    DLog("No fallback match found in resources");
                }
            }

            if (stream == null)
            {
                DLog("FAIL: WinOptimizerAgent.exe resource NOT FOUND in assembly!");
                return false;
            }

            DLog($"Resource stream length: {stream.Length} bytes ({stream.Length / (1024 * 1024)}MB)");

            // Write to disk
            using var fileStream = new FileStream(AgentExePath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fileStream);
            await fileStream.FlushAsync();

            var fileInfo = new FileInfo(AgentExePath);
            DLog($"Extracted to: {AgentExePath} ({fileInfo.Length / (1024 * 1024)}MB)");
            DLog($"File exists: {fileInfo.Exists}, size: {fileInfo.Length}");
            return true;
        }
        catch (Exception ex)
        {
            DLog($"EXTRACT ERROR: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Створити Windows Scheduled Task для агента.
    /// </summary>
    private static async Task<bool> CreateScheduledTaskAsync()
    {
        try
        {
            // Delete existing task first
            var delOutput = await RunPsAsync(
                $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue");
            DLog($"Delete old task: done");

            // Create new task
            var psCmd =
                $"$action = New-ScheduledTaskAction -Execute '{AgentExePath}'; " +
                $"$t1 = New-ScheduledTaskTrigger -AtStartup; $t2 = New-ScheduledTaskTrigger -AtLogOn; $trigger = @($t1, $t2); " +
                $"$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                    $"-StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); " +
                $"$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; " +
                $"Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger " +
                    $"-Settings $settings -Principal $principal -Force";

            var output = await RunPsAsync(psCmd);
            DLog($"Create task output: {output.Trim()}");
            return true;
        }
        catch (Exception ex)
        {
            DLog($"TASK CREATE ERROR: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Запустити агент одразу.
    /// </summary>
    private static async Task<bool> StartAgentAsync()
    {
        try
        {
            // Try via scheduled task first (runs as SYSTEM)
            var output = await RunPsAsync(
                $"Start-ScheduledTask -TaskName '{TaskName}' -ErrorAction Stop");
            DLog($"Started via scheduled task: {output.Trim()}");
            return true;
        }
        catch (Exception ex)
        {
            DLog($"Scheduled task start failed: {ex.Message}");

            // Fallback: start directly
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = AgentExePath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                DLog($"Started directly: PID={proc?.Id ?? -1}");
                return proc != null;
            }
            catch (Exception ex2)
            {
                DLog($"Direct start failed: {ex2.Message}");
                return false;
            }
        }
    }

    private static async Task<string> RunPsAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -NoLogo -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(stderr))
                DLog($"PS stderr: {stderr.Trim()}");

            return stdout;
        }
        catch (Exception ex)
        {
            DLog($"PS exec error: {ex.Message}");
            return "";
        }
    }
}
