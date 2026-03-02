using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Rollback;

/// <summary>
/// Windows System Restore Point — створення, відновлення, видалення.
/// </summary>
public static class SystemRestoreService
{
    // Desktop лог для діагностики
    private static readonly string DesktopLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        "WinOptimizer_Deploy.log");
    private static void DLog(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [RESTORE] {msg}";
        Logger.Info($"[RESTORE] {msg}");
        try { File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
    }

    /// <summary>
    /// Створити System Restore Point. Повертає sequence number.
    /// </summary>
    public static async Task<int> CreateRestorePointAsync(string description)
    {
        DLog("=== CREATE RESTORE POINT ===");

        // 1. Disable 24h throttle
        var throttle = await RunPsAsync(
            "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' " +
            "-Name 'SystemRestorePointCreationFrequency' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue");
        DLog($"Throttle disabled: OK");

        // 2. Enable System Restore if disabled
        var enable = await RunPsAsync("Enable-ComputerRestore -Drive 'C:\\' -ErrorAction SilentlyContinue");
        DLog($"Enable-ComputerRestore: OK");

        // 2.5: Check if System Restore is enabled
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
            DLog($"✅ Restore point created: #{seq}");
            return seq;
        }

        DLog($"❌ Failed to parse sequence number from: '{output.Trim()}'");
        throw new InvalidOperationException($"Failed to get restore point sequence number. Output: '{output.Trim()}'");
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
            psi.FileName = "powershell.exe";
        }

        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
    }
}
