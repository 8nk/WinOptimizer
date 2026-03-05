using System.Diagnostics;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

/// <summary>
/// Аналіз заліза та рекомендація оптимальної Windows.
/// </summary>
public static class HardwareAnalyzer
{
    public class HardwareInfo
    {
        public string CpuName { get; set; } = "Unknown";
        public int CpuCores { get; set; }
        public int CpuGeneration { get; set; }
        public long RamMB { get; set; }
        public bool IsSsd { get; set; }
        public long DiskSizeGB { get; set; }
        public string GpuName { get; set; } = "Unknown";
        public bool HasTpm2 { get; set; }
        public bool IsUefi { get; set; }
        public string CurrentWindows { get; set; } = "Unknown";
        public string CurrentBuild { get; set; } = "";
        public string RecommendedWindows { get; set; } = "Windows 10";
        public string RecommendationReason { get; set; } = "";
    }

    public static async Task<HardwareInfo> AnalyzeAsync()
    {
        var info = new HardwareInfo();

        var tasks = new List<Task>
        {
            Task.Run(() => GetCpuInfo(info)),
            Task.Run(() => GetRamInfo(info)),
            Task.Run(() => GetDiskInfo(info)),
            Task.Run(() => GetGpuInfo(info)),
            Task.Run(() => GetTpmInfo(info)),
            Task.Run(() => GetWindowsInfo(info)),
            Task.Run(() => GetUefiInfo(info))
        };

        await Task.WhenAll(tasks);

        DetermineRecommendation(info);

        Logger.Info($"Hardware: {info.CpuName}, {info.RamMB}MB RAM, " +
                    $"SSD={info.IsSsd}, TPM2={info.HasTpm2}, UEFI={info.IsUefi}");
        Logger.Info($"Recommendation: {info.RecommendedWindows} ({info.RecommendationReason})");

        return info;
    }

    private static void GetCpuInfo(HardwareInfo info)
    {
        try
        {
            var output = RunPs("(Get-WmiObject Win32_Processor | Select-Object -First 1).Name");
            info.CpuName = output.Trim();

            var coresOutput = RunPs("(Get-WmiObject Win32_Processor | Select-Object -First 1).NumberOfCores");
            if (int.TryParse(coresOutput.Trim(), out var cores))
                info.CpuCores = cores;

            // Determine Intel generation from CPU name
            info.CpuGeneration = DetectCpuGeneration(info.CpuName);
        }
        catch (Exception ex) { Logger.Warn($"CPU info error: {ex.Message}"); }
    }

    private static int DetectCpuGeneration(string cpuName)
    {
        var upper = cpuName.ToUpperInvariant();

        // Intel: "Core i5-10400" → gen 10, "Core i7-8700" → gen 8
        if (upper.Contains("INTEL") || upper.Contains("CORE"))
        {
            // Match pattern like i5-XXYY or i7-XXYY
            var idx = upper.IndexOf("-");
            if (idx > 0 && idx + 2 < upper.Length)
            {
                var afterDash = upper[(idx + 1)..];
                // First 1-2 digits before the model number = generation
                var genStr = "";
                foreach (var c in afterDash)
                {
                    if (char.IsDigit(c)) genStr += c;
                    else break;
                }
                if (genStr.Length >= 4 && int.TryParse(genStr[..2], out var gen2))
                    return gen2;
                if (genStr.Length >= 3 && int.TryParse(genStr[..1], out var gen1))
                    return gen1;
            }
        }

        // AMD: "Ryzen 5 3600" → Zen2 (gen ~3), "Ryzen 7 5800X" → Zen3
        if (upper.Contains("RYZEN"))
        {
            if (upper.Contains("7000") || upper.Contains("7X") || upper.Contains("9000")) return 13;
            if (upper.Contains("5000") || upper.Contains("5X")) return 11;
            if (upper.Contains("3000") || upper.Contains("3X")) return 10;
            if (upper.Contains("2000") || upper.Contains("2X")) return 9;
            if (upper.Contains("1000") || upper.Contains("1X")) return 8;
        }

        return 0; // Unknown
    }

    private static void GetRamInfo(HardwareInfo info)
    {
        try
        {
            var output = RunPs("[math]::Round((Get-WmiObject Win32_ComputerSystem).TotalPhysicalMemory / 1MB)");
            if (long.TryParse(output.Trim(), out var mb))
                info.RamMB = mb;
        }
        catch (Exception ex) { Logger.Warn($"RAM info error: {ex.Message}"); }
    }

    private static void GetDiskInfo(HardwareInfo info)
    {
        try
        {
            var typeOutput = RunPs("(Get-PhysicalDisk | Select-Object -First 1).MediaType");
            info.IsSsd = typeOutput.Trim().Contains("SSD", StringComparison.OrdinalIgnoreCase);

            var sizeOutput = RunPs("[math]::Round((Get-PhysicalDisk | Select-Object -First 1).Size / 1GB)");
            if (long.TryParse(sizeOutput.Trim(), out var gb))
                info.DiskSizeGB = gb;
        }
        catch (Exception ex) { Logger.Warn($"Disk info error: {ex.Message}"); }
    }

    private static void GetGpuInfo(HardwareInfo info)
    {
        try
        {
            var output = RunPs("(Get-WmiObject Win32_VideoController | Select-Object -First 1).Name");
            info.GpuName = output.Trim();
        }
        catch (Exception ex) { Logger.Warn($"GPU info error: {ex.Message}"); }
    }

    private static void GetTpmInfo(HardwareInfo info)
    {
        try
        {
            var output = RunPs(
                "try { $tpm = Get-WmiObject -Namespace 'root\\cimv2\\Security\\MicrosoftTpm' -Class Win32_Tpm -ErrorAction Stop; " +
                "if($tpm) { $tpm.SpecVersion.Split(',')[0].Trim() } else { '0' } } catch { '0' }");
            var ver = output.Trim();
            info.HasTpm2 = ver.StartsWith("2");
        }
        catch { info.HasTpm2 = false; }
    }

    private static void GetWindowsInfo(HardwareInfo info)
    {
        try
        {
            var caption = RunPs("(Get-WmiObject Win32_OperatingSystem).Caption");
            info.CurrentWindows = caption.Trim();

            var build = RunPs("[System.Environment]::OSVersion.Version.Build");
            info.CurrentBuild = build.Trim();
        }
        catch (Exception ex) { Logger.Warn($"Windows info error: {ex.Message}"); }
    }

    private static void GetUefiInfo(HardwareInfo info)
    {
        try
        {
            var output = RunPs(
                "try { $env:firmware_type } catch { 'Unknown' }; " +
                "if(-not $env:firmware_type) { " +
                "  if(Test-Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State') { 'UEFI' } " +
                "  else { bcdedit /enum | Select-String 'path.*efi' | ForEach-Object { 'UEFI' } } }");
            info.IsUefi = output.Contains("UEFI", StringComparison.OrdinalIgnoreCase);
        }
        catch { info.IsUefi = false; }
    }

    private static void DetermineRecommendation(HardwareInfo info)
    {
        // Windows 11 requirements: TPM 2.0 + 8th gen Intel/Zen2+ + 4GB+ RAM + UEFI + 64GB+ disk
        bool meetsWin11 = info.HasTpm2 &&
                          info.CpuGeneration >= 8 &&
                          info.RamMB >= 4000 &&
                          info.IsUefi &&
                          info.DiskSizeGB >= 64;

        if (meetsWin11)
        {
            info.RecommendedWindows = "Windows 11";
            info.RecommendationReason = "TPM 2.0, UEFI, сучасний процесор";
        }
        else if (info.RamMB >= 2000)
        {
            info.RecommendedWindows = "Windows 10";
            var reasons = new List<string>();
            if (!info.HasTpm2) reasons.Add("немає TPM 2.0");
            if (!info.IsUefi) reasons.Add("Legacy BIOS");
            if (info.CpuGeneration < 8 && info.CpuGeneration > 0) reasons.Add($"CPU gen {info.CpuGeneration}");
            info.RecommendationReason = reasons.Count > 0
                ? string.Join(", ", reasons)
                : "оптимальна для вашого обладнання";
        }
        else
        {
            info.RecommendedWindows = "Windows 10 LTSC";
            info.RecommendationReason = $"мало RAM ({info.RamMB}MB)";
        }
    }

    private static string RunPs(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellHelper.Path,
            Arguments = $"-NoProfile -NoLogo -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; {command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);
        return output;
    }
}
