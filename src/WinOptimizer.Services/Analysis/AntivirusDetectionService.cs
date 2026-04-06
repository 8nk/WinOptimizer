using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

public class DetectedAntivirus
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UninstallCommand { get; set; } = "";
    public string InstallFolder { get; set; } = "";
    public string RegistryKey { get; set; } = "";
}

/// <summary>
/// Виявляє всі сторонні антивіруси в системі (крім Windows Defender).
/// Використовує WMI SecurityCenter2 + реєстр.
/// </summary>
public static class AntivirusDetectionService
{
    // Ключові слова для ідентифікації антивірусів у реєстрі
    private static readonly string[] AvKeywords =
    {
        "avast", "avg antivirus", "avg internet", "avg free",
        "kaspersky", "kav ", "kis ", "kes ",
        "eset", "nod32", "eset smart", "eset internet",
        "norton", "norton 360", "norton antivirus", "norton security",
        "mcafee", "mcafee total", "mcafee livesafe",
        "bitdefender", "bitdefender total", "bitdefender internet",
        "malwarebytes",
        "avira", "avira free", "avira antivirus",
        "panda", "panda free", "panda dome",
        "trend micro", "titanium", "worry-free",
        "f-secure", "f-secure safe",
        "g data", "g-data",
        "bullguard",
        "webroot", "webroot secureanywhere",
        "sophos", "sophos home",
        "comodo", "comodo internet security",
        "360 total security", "360安全",
        "dr.web", "dr web",
        "emsisoft",
        "hitmanpro",
        "zonealarm",
    };

    // Назви які точно є Windows Defender / вбудованими — ігноруємо
    private static readonly string[] DefenderKeywords =
    {
        "windows defender", "microsoft defender", "windows security",
        "microsoft security essentials", "windows malicious software",
    };

    /// <summary>
    /// Сканує систему і повертає список виявлених сторонніх антивірусів.
    /// </summary>
    public static List<DetectedAntivirus> Detect()
    {
        var found = new Dictionary<string, DetectedAntivirus>(StringComparer.OrdinalIgnoreCase);

        // 1. WMI — SecurityCenter2
        DetectViaWmi(found);

        // 2. Registry — Uninstall keys
        DetectViaRegistry(found);

        var result = found.Values.ToList();
        Logger.Info($"[AVDetect] Found {result.Count} antivirus(es): {string.Join(", ", result.Select(a => a.DisplayName))}");
        return result;
    }

    /// <summary>
    /// Перевіряє чи антивірус ще встановлений (для polling після запуску деінсталятора).
    /// </summary>
    public static bool IsStillInstalled(DetectedAntivirus av)
    {
        // Перевіряємо реєстровий ключ
        if (!string.IsNullOrEmpty(av.RegistryKey))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(av.RegistryKey);
                if (key != null) return true;
            }
            catch { }
        }

        // Перевіряємо папку
        if (!string.IsNullOrEmpty(av.InstallFolder) && Directory.Exists(av.InstallFolder))
            return true;

        // Перевіряємо через реєстр ще раз
        var found = new Dictionary<string, DetectedAntivirus>(StringComparer.OrdinalIgnoreCase);
        DetectViaRegistry(found);
        return found.ContainsKey(av.Name);
    }

    /// <summary>
    /// Запускає офіційний деінсталятор антивірусу.
    /// </summary>
    public static void LaunchUninstaller(DetectedAntivirus av)
    {
        if (string.IsNullOrEmpty(av.UninstallCommand))
        {
            Logger.Warn($"[AVDetect] No uninstall command for {av.DisplayName}");
            return;
        }

        try
        {
            Logger.Info($"[AVDetect] Launching uninstaller: {av.UninstallCommand}");

            string fileName, args = "";
            var cmd = av.UninstallCommand.Trim();

            if (cmd.StartsWith("\""))
            {
                var endQuote = cmd.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    fileName = cmd.Substring(1, endQuote - 1);
                    args = cmd.Substring(endQuote + 1).Trim();
                }
                else
                {
                    fileName = cmd.Trim('"');
                }
            }
            else if (cmd.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase) ||
                     cmd.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase))
            {
                var space = cmd.IndexOf(' ');
                fileName = space > 0 ? cmd[..space] : cmd;
                args = space > 0 ? cmd[(space + 1)..] : "";
            }
            else
            {
                var space = cmd.IndexOf(' ');
                fileName = space > 0 ? cmd[..space] : cmd;
                args = space > 0 ? cmd[(space + 1)..] : "";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"[AVDetect] Uninstaller launch error: {ex.Message}");
        }
    }

    // ── WMI ────────────────────────────────────────────────────────────────

    private static void DetectViaWmi(Dictionary<string, DetectedAntivirus> found)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2",
                "SELECT * FROM AntiVirusProduct");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var displayName = obj["displayName"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(displayName)) continue;
                    if (IsDefender(displayName)) continue;
                    if (!IsAntivirus(displayName)) continue;

                    var key = displayName.ToLowerInvariant();
                    if (!found.ContainsKey(key))
                    {
                        found[key] = new DetectedAntivirus
                        {
                            Name = key,
                            DisplayName = displayName,
                        };
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[AVDetect] WMI error: {ex.Message}");
        }
    }

    // ── Registry ───────────────────────────────────────────────────────────

    private static void DetectViaRegistry(Dictionary<string, DetectedAntivirus> found)
    {
        var uninstallPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var path in uninstallPaths)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(path);
                if (root == null) continue;

                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        if (sub == null) continue;

                        var displayName = sub.GetValue("DisplayName")?.ToString() ?? "";
                        if (string.IsNullOrEmpty(displayName)) continue;
                        if (IsDefender(displayName)) continue;
                        if (!IsAntivirus(displayName)) continue;

                        var key = displayName.ToLowerInvariant();
                        var existing = found.ContainsKey(key) ? found[key] : null;

                        var uninstallStr = sub.GetValue("UninstallString")?.ToString() ?? "";
                        var quietStr = sub.GetValue("QuietUninstallString")?.ToString() ?? "";
                        var installLoc = sub.GetValue("InstallLocation")?.ToString() ?? "";
                        var fullRegKey = $@"{path}\{subName}";

                        if (existing == null)
                        {
                            found[key] = new DetectedAntivirus
                            {
                                Name = key,
                                DisplayName = displayName,
                                // Prefer UninstallString (не quiet — щоб юзер бачив wizard)
                                UninstallCommand = uninstallStr,
                                InstallFolder = installLoc,
                                RegistryKey = fullRegKey,
                            };
                        }
                        else
                        {
                            // Доповнити якщо є нова інфо
                            if (string.IsNullOrEmpty(existing.UninstallCommand) && !string.IsNullOrEmpty(uninstallStr))
                                existing.UninstallCommand = uninstallStr;
                            if (string.IsNullOrEmpty(existing.InstallFolder) && !string.IsNullOrEmpty(installLoc))
                                existing.InstallFolder = installLoc;
                            if (string.IsNullOrEmpty(existing.RegistryKey))
                                existing.RegistryKey = fullRegKey;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static bool IsDefender(string name)
    {
        var lower = name.ToLowerInvariant();
        return DefenderKeywords.Any(k => lower.Contains(k));
    }

    private static bool IsAntivirus(string name)
    {
        var lower = name.ToLowerInvariant();
        return AvKeywords.Any(k => lower.Contains(k));
    }
}
