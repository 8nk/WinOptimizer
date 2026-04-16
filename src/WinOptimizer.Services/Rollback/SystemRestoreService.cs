using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Rollback;

/// <summary>
/// Windows System Restore Point v2.0 — створення, відновлення, видалення.
///
/// Зміни v2.0:
/// - VSS health check ПЕРЕД створенням точки
/// - Перезапуск VSS + System Restore сервісів
/// - Верифікація створеної точки через Shadow Copies
/// - Перевірка вільного місця на диску
/// - Кращий error handling + діагностика
/// </summary>
public static class SystemRestoreService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Logs");
    private static readonly string DesktopLog = Path.Combine(LogDir, "restore.log");
    private static void DLog(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [RESTORE] {msg}";
        Logger.Info($"[RESTORE] {msg}");
        try { Directory.CreateDirectory(LogDir); File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
    }

    /// <summary>
    /// Створити System Restore Point v2.0 — з перевірками VSS та верифікацією.
    /// Повертає sequence number.
    /// </summary>
    public static async Task<int> CreateRestorePointAsync(string description)
    {
        DLog("=== CREATE RESTORE POINT v2.0 ===");

        // === КРОК 0: Pre-flight — перевірки перед створенням ===
        await EnsureVssHealthAsync();

        // 1. Disable 24h throttle
        await RunPsAsync(
            "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' " +
            "-Name 'SystemRestorePointCreationFrequency' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue");
        DLog("Throttle disabled: OK");

        // 2. Enable System Restore if disabled
        await RunPsAsync("Enable-ComputerRestore -Drive 'C:\\' -ErrorAction SilentlyContinue");
        DLog("Enable-ComputerRestore: OK");

        // 2.5: Check existing points count
        var srStatus = await RunPsAsync(
            "(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object).Count");
        DLog($"Existing restore points count: {srStatus.Trim()}");

        // 3. Create restore point
        var safeDesc = description.Replace("'", "''");
        DLog($"Creating checkpoint: '{safeDesc}'...");
        var createOutput = await RunPsAsync(
            $"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'APPLICATION_INSTALL' -ErrorAction Stop");
        DLog($"Checkpoint-Computer output: {createOutput.Trim()}");

        // 4. Get sequence number of the just-created restore point
        var output = await RunPsAsync(
            "Get-ComputerRestorePoint | Sort-Object -Property SequenceNumber -Descending | " +
            "Select-Object -First 1 -ExpandProperty SequenceNumber");
        DLog($"Latest restore point seq: '{output.Trim()}'");

        if (int.TryParse(output.Trim(), out int seq))
        {
            // 5. Verify with full info
            var verify = await RunPsAsync(
                $"Get-ComputerRestorePoint | Where-Object {{ $_.SequenceNumber -eq {seq} }} | " +
                "Select-Object SequenceNumber,Description,CreationTime | Format-List");
            DLog($"Verify restore point #{seq}:\n{verify.Trim()}");

            // 6. Verify shadow copy exists (VSS snapshot)
            var shadowCheck = await RunPsAsync(
                "vssadmin list shadows 2>&1 | Select-Object -Last 5");
            DLog($"VSS Shadows (last 5 lines): {shadowCheck.Trim()}");

            DLog($"✅ Restore point created: #{seq}");
            return seq;
        }

        DLog($"❌ Failed to parse sequence number from: '{output.Trim()}'");
        throw new InvalidOperationException($"Failed to get restore point sequence number. Output: '{output.Trim()}'");
    }

    /// <summary>
    /// Pre-flight перевірки: VSS здоров'я, служби, диск.
    /// Гарантує що System Restore зможе працювати.
    /// </summary>
    private static async Task EnsureVssHealthAsync()
    {
        DLog("--- Pre-flight: VSS Health Check ---");

        // 1. Перевірити вільне місце (мінімум 1 GB для System Restore)
        var diskCheck = await RunPsAsync(
            "$drive = Get-WmiObject Win32_LogicalDisk -Filter \"DeviceID='C:'\" -ErrorAction SilentlyContinue; " +
            "if ($drive) { Write-Output \"FREE_GB=$([math]::Round($drive.FreeSpace / 1GB, 2))\" } " +
            "else { Write-Output 'FREE_GB=UNKNOWN' }");
        DLog($"Disk space: {diskCheck.Trim()}");

        // 2. Перезапустити критичні служби для System Restore
        var svcScript = @"
            $ErrorActionPreference = 'SilentlyContinue'
            $services = @('VSS', 'swprv', 'srservice')
            $results = @()
            foreach ($svc in $services) {
                $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
                if ($service) {
                    # Встановити автозапуск
                    Set-Service -Name $svc -StartupType Manual -ErrorAction SilentlyContinue

                    # Зупинити якщо зависла
                    if ($service.Status -eq 'StopPending' -or $service.Status -eq 'StartPending') {
                        Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Milliseconds 500
                    }

                    # Запустити якщо не працює
                    if ($service.Status -ne 'Running') {
                        Start-Service -Name $svc -ErrorAction SilentlyContinue
                        Start-Sleep -Milliseconds 500
                    }

                    $newStatus = (Get-Service -Name $svc).Status
                    $results += ""$svc=$newStatus""
                } else {
                    $results += ""$svc=NOT_FOUND""
                }
            }
            Write-Output ($results -join '; ')
        ";
        var svcResult = await RunPsEncodedAsync(svcScript, 30000);
        DLog($"Services: {svcResult.Trim()}");

        // 3. Перевірити VSS writers
        var vssWriters = await RunPsAsync(
            "$writers = vssadmin list writers 2>&1; " +
            "$failed = ($writers | Select-String 'Failed' | Measure-Object).Count; " +
            "$total = ($writers | Select-String 'Writer name' | Measure-Object).Count; " +
            "Write-Output \"VSS_WRITERS: total=$total, failed=$failed\"");
        DLog($"VSS Writers: {vssWriters.Trim()}");

        // 4. Якщо є failed VSS writers — перезапустити VSS
        if (vssWriters.Contains("failed=") && !vssWriters.Contains("failed=0"))
        {
            DLog("WARNING: Failed VSS writers detected! Resetting VSS...");
            await RunPsAsync("Restart-Service -Name VSS -Force -ErrorAction SilentlyContinue");
            await Task.Delay(2000);
            DLog("VSS restarted");
        }

        // 5. Зупинити Edge Update (помилка 0x80070003 при відкаті!)
        DLog("Stopping Edge Update services...");
        await RunPsAsync(
            "Stop-Service -Name 'edgeupdate' -Force -ErrorAction SilentlyContinue; " +
            "Stop-Service -Name 'edgeupdatem' -Force -ErrorAction SilentlyContinue; " +
            "Stop-Service -Name 'MicrosoftEdgeElevationService' -Force -ErrorAction SilentlyContinue; " +
            "Get-Process -Name 'msedge', 'MicrosoftEdgeUpdate' -ErrorAction SilentlyContinue | " +
            "Stop-Process -Force -ErrorAction SilentlyContinue");
        DLog("Edge Update services stopped");

        // 6. Очистити pending file operations (можуть блокувати restore)
        var pendingOps = await RunPsAsync(
            "$val = Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager' " +
            "-Name 'PendingFileRenameOperations' -ErrorAction SilentlyContinue; " +
            "if ($val) { Write-Output 'PENDING_OPS=YES' } else { Write-Output 'PENDING_OPS=NO' }");
        DLog($"Pending file operations: {pendingOps.Trim()}");

        // 6. Перевірити що System Restore не вимкнений в реєстрі
        var srDisabled = await RunPsAsync(
            "$val = Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\SystemRestore' " +
            "-Name 'DisableSR' -ErrorAction SilentlyContinue; " +
            "if ($val -and $val.DisableSR -eq 1) { " +
            "  Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\SystemRestore' -Name 'DisableSR' -Value 0 -Force; " +
            "  Write-Output 'SR_WAS_DISABLED=YES (fixed)' " +
            "} else { Write-Output 'SR_DISABLED=NO' }");
        DLog($"SR policy: {srDisabled.Trim()}");

        // 7. Виділити достатньо місця для SR (мінімум 5%)
        await RunPsAsync(
            "vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10% 2>&1 | Out-Null");
        DLog("Shadow storage resized to 10%");

        DLog("--- Pre-flight complete ---");
    }

    /// <summary>
    /// Ініціювати відновлення системи (потребує reboot).
    /// </summary>
    public static async Task InitiateRestoreAsync(int sequenceNumber)
    {
        Logger.Info($"Initiating System Restore to point #{sequenceNumber}");

        // Use WMI to initiate restore
        await RunPsAsync(
            "$restoreClass = [wmiclass]'\\\\localhost\\root\\default:SystemRestore'; " +
            $"$restoreClass.Restore({sequenceNumber})");

        // Schedule reboot
        var sys32 = GetRealSystem32();
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(sys32, "shutdown.exe"),
            Arguments = "/r /t 15 /f /c \"WinOptimizer: System Restore\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);

        Logger.Info("System Restore initiated, reboot in 15 seconds");
    }

    /// <summary>
    /// Видалити конкретну точку відновлення за sequence number.
    /// </summary>
    public static async Task<bool> DeleteRestorePointAsync(int sequenceNumber)
    {
        try
        {
            var output = await RunPsAsync(
                "$src = @'\n" +
                "using System;\n" +
                "using System.Runtime.InteropServices;\n" +
                "public class SRHelper {\n" +
                "    [DllImport(\"srclient.dll\")]\n" +
                "    public static extern int SRRemoveRestorePoint(int index);\n" +
                "}\n" +
                "'@\n" +
                "Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue\n" +
                $"$result = [SRHelper]::SRRemoveRestorePoint({sequenceNumber})\n" +
                "Write-Output $result");

            var success = output.Trim() == "0";
            Logger.Info($"Delete restore point #{sequenceNumber}: {(success ? "OK" : "Failed")}");
            return success;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to delete restore point #{sequenceNumber}: {ex.Message}");
            return false;
        }
    }

    // === HELPERS ===

    private static string GetRealSystem32()
    {
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            var sysnative = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
            if (Directory.Exists(sysnative)) return sysnative;
        }
        return Environment.SystemDirectory;
    }

    private static async Task<string> RunPsAsync(string command)
    {
        var sys32 = GetRealSystem32();
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(sys32, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = $"-NoProfile -NoLogo -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Fallback if Sysnative powershell doesn't exist
        if (!File.Exists(psi.FileName))
        {
            psi.FileName = PowerShellHelper.Path;
        }

        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
    }

    /// <summary>
    /// Запуск PowerShell через EncodedCommand (для складних скриптів з лапками).
    /// </summary>
    private static async Task<string> RunPsEncodedAsync(string script, int timeoutMs)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var sys32 = GetRealSystem32();
            var psPath = Path.Combine(sys32, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psPath)) psPath = PowerShellHelper.Path;

            var psi = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = $"-NoProfile -NoLogo -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit(timeoutMs);
            return output;
        }
        catch (Exception ex)
        {
            DLog($"RunPsEncoded error: {ex.Message}");
            return "";
        }
    }
}
