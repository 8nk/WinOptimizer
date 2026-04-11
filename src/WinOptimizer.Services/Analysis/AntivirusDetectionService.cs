using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Analysis;

public class DetectedAntivirus
{
    public string Name { get; set; } = "";          // нормалізований бренд-ключ ("avast")
    public string DisplayName { get; set; } = "";   // повна назва для UI
    public string UninstallCommand { get; set; } = "";
    public string InstallFolder { get; set; } = "";
    public string RegistryKey { get; set; } = "";
}

/// <summary>
/// Виявляє всі сторонні антивіруси (крім Windows Defender).
/// Ключ нормалізується по бренду — "Avast Antivirus" і "Avast Free Antivirus"
/// → один запис з ключем "avast".
/// </summary>
public static class AntivirusDetectionService
{
    // Порядок важливий — перевіряємо від більш специфічних до загальних
    private static readonly string[] AvBrands =
    {
        "avast",
        "avg",
        "kaspersky",
        "eset",
        "norton",
        "mcafee",
        "bitdefender",
        "malwarebytes",
        "avira",
        "panda",
        "trend micro",
        "f-secure",
        "g data",
        "g-data",
        "bullguard",
        "webroot",
        "sophos",
        "comodo",
        "360 total security",
        "dr.web",
        "drweb",
        "emsisoft",
        "hitmanpro",
        "zonealarm",
        "vipre",
        "cylance",
        "crowdstrike",
        "sentinel one",
        "sentinelone",
        "360安全",
    };

    private static readonly string[] DefenderKeywords =
    {
        "windows defender", "microsoft defender", "windows security",
        "microsoft security essentials", "windows malicious software",
    };

    /// <summary>
    /// Витягти бренд-ключ з назви програми.
    /// "Avast Free Antivirus" → "avast"
    /// "Kaspersky Internet Security" → "kaspersky"
    /// Повертає null якщо не розпізнано.
    /// </summary>
    private static string? ExtractBrand(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var lower = displayName.ToLowerInvariant();
        if (DefenderKeywords.Any(d => lower.Contains(d))) return null; // Ігноруємо Defender
        foreach (var brand in AvBrands)
        {
            if (lower.Contains(brand)) return brand;
        }
        return null;
    }

    /// <summary>
    /// Сканує систему і повертає список виявлених сторонніх антивірусів.
    /// Записи злиті по бренду — один запис на антивірус.
    /// </summary>
    public static List<DetectedAntivirus> Detect()
    {
        // Ключ = бренд ("avast", "kaspersky", ...)
        var found = new Dictionary<string, DetectedAntivirus>(StringComparer.OrdinalIgnoreCase);

        // 1. WMI — дає display name (але часто без UninstallCommand)
        DetectViaWmi(found);

        // 2. Registry — дає UninstallString, InstallLocation тощо
        //    Якщо запис вже є (по бренду) — доповнюємо UninstallCommand/InstallFolder
        DetectViaRegistry(found);

        var result = found.Values.ToList();
        Logger.Info($"[AVDetect] Found {result.Count}: {string.Join(", ", result.Select(a => $"{a.DisplayName}(cmd:{!string.IsNullOrEmpty(a.UninstallCommand)})"))}");
        return result;
    }

    /// <summary>
    /// Перевіряє чи антивірус ще встановлений.
    /// Використовує повторний scan — надійний, не залежить від збережених даних.
    /// </summary>
    public static bool IsStillInstalled(DetectedAntivirus av)
    {
        // Авторитетне джерело: WMI SecurityCenter2 — якщо AV там є, він активний
        // Якщо WMI не знає — вважаємо видаленим (не чекаємо на папки/залишки)
        var freshFound = new Dictionary<string, DetectedAntivirus>(StringComparer.OrdinalIgnoreCase);
        try { DetectViaWmi(freshFound); } catch { }

        if (freshFound.ContainsKey(av.Name))
            return true;

        // Підтвердження через registry (64-bit view) — перевіряємо чи ключ ще є
        var uninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = hklm.OpenSubKey(uninstallSubKey);
            if (root != null)
            {
                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        var dn = sub?.GetValue("DisplayName")?.ToString() ?? "";
                        if (ExtractBrand(dn) == av.Name) return true;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Запускає офіційний деінсталятор.
    /// Якщо UninstallCommand порожній — відкриває "Програми і компоненти".
    /// Повертає Process деінсталятора (для відстеження виходу) або null.
    /// </summary>
    public static Process? LaunchUninstaller(DetectedAntivirus av)
    {
        Logger.Info($"[AVDetect] LaunchUninstaller: {av.DisplayName}, cmd='{av.UninstallCommand}'");

        if (string.IsNullOrEmpty(av.UninstallCommand))
        {
            // 1. Динамічний пошук в Program Files по бренду
            var dynamic = FindUninstallerDynamic(av.Name, av.InstallFolder);
            if (dynamic != null)
            {
                Logger.Info($"[AVDetect] Dynamic uninstaller found: {dynamic.Value.Path} {dynamic.Value.Args}");
                return TryLaunchProcess(dynamic.Value.Path, dynamic.Value.Args);
            }

            // 2. Останній fallback: відкриваємо "Програми і компоненти"
            Logger.Warn($"[AVDetect] No uninstaller found for {av.DisplayName}, opening appwiz.cpl");
            return TryLaunchProcess("control.exe", "appwiz.cpl");
        }

        // Парсимо UninstallCommand
        var cmd = av.UninstallCommand.Trim();
        string fileName, args = "";

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
        else
        {
            var space = cmd.IndexOf(' ');
            fileName = space > 0 ? cmd[..space] : cmd;
            args = space > 0 ? cmd[(space + 1)..] : "";
        }

        // Прибираємо silent/quiet флаги — нам потрібен GUI щоб юзер бачив wizard
        args = RemoveSilentFlags(args);

        return TryLaunchProcess(fileName, args);
    }

    // ── Аліаси брендів для пошуку в Program Files ─────────────────────────
    private static readonly Dictionary<string, string[]> BrandAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["avast"]        = new[] { "avast" },
            ["avg"]          = new[] { "avg" },
            ["kaspersky"]    = new[] { "kaspersky" },
            ["eset"]         = new[] { "eset" },
            ["norton"]       = new[] { "norton", "nortonlifelock" },
            ["mcafee"]       = new[] { "mcafee" },
            ["bitdefender"]  = new[] { "bitdefender" },
            ["malwarebytes"] = new[] { "malwarebytes" },
            ["avira"]        = new[] { "avira" },
            ["360 total security"] = new[] { "360" },
            ["comodo"]       = new[] { "comodo" },
            ["sophos"]       = new[] { "sophos" },
            ["trend micro"]  = new[] { "trend micro", "trendmicro" },
            ["webroot"]      = new[] { "webroot" },
            ["emsisoft"]     = new[] { "emsisoft" },
            ["crowdstrike"]  = new[] { "crowdstrike" },
            ["sentinelone"]  = new[] { "sentinelone", "sentinel one" },
        };

    // Аргументи для специфічних виконуваних файлів деінсталяторів
    private static readonly Dictionary<string, string> UninstallerExeArgs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Avast/AVG: instup.exe /control_panel → відкриває GUI wizard
            ["instup.exe"]   = "/control_panel",
            // Kaspersky: avpui.exe без аргументів — само відкриває uninstall UI
            ["avpui.exe"]    = "",
        };

    /// <summary>
    /// Динамічно шукає деінсталятор: InstallFolder → Program Files → Program Files (x86).
    /// Повертає (path, args) або null.
    /// </summary>
    private static (string Path, string Args)? FindUninstallerDynamic(string brand, string? installFolder)
    {
        var keywords = BrandAliases.TryGetValue(brand, out var aliases) ? aliases : new[] { brand };

        var searchRoots = new List<string>();
        if (!string.IsNullOrEmpty(installFolder) && Directory.Exists(installFolder))
            searchRoots.Add(installFolder);
        searchRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        searchRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        foreach (var root in searchRoots.Distinct())
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> dirsToCheck;
            if (root == installFolder)
            {
                dirsToCheck = new[] { root };
            }
            else
            {
                try
                {
                    dirsToCheck = Directory.GetDirectories(root)
                        .Where(d => keywords.Any(k =>
                            Path.GetFileName(d).Contains(k, StringComparison.OrdinalIgnoreCase)));
                }
                catch { continue; }
            }

            foreach (var brandDir in dirsToCheck)
            {
                var found = SearchUninstallerInDir(brandDir);
                if (found != null)
                {
                    var exeName = Path.GetFileName(found);
                    var args = UninstallerExeArgs.TryGetValue(exeName, out var knownArgs) ? knownArgs : "";
                    return (found, args);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Шукає деінсталятор в директорії (глибина 2).
    /// Пріоритет: uninstall*.exe > uninst*.exe > inins*.exe > setup/instup.exe > setup/*.exe
    /// </summary>
    private static string? SearchUninstallerInDir(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        // Рівень 0: пряма папка
        try
        {
            var direct = Directory.GetFiles(dir, "uninstall*.exe", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(dir, "uninst*.exe",    SearchOption.TopDirectoryOnly))
                .Concat(Directory.GetFiles(dir, "inins*.exe",     SearchOption.TopDirectoryOnly))
                .ToArray();
            if (direct.Length > 0) return direct[0];
        }
        catch { }

        // setup/ підпапка — instup.exe (Avast/AVG) або *setup*.exe
        try
        {
            var setupDir = Path.Combine(dir, "setup");
            if (Directory.Exists(setupDir))
            {
                // instup.exe — офіційний деінсталятор Avast/AVG
                var instup = Path.Combine(setupDir, "instup.exe");
                if (File.Exists(instup)) return instup;

                var setupExes = Directory.GetFiles(setupDir, "*setup*.exe", SearchOption.TopDirectoryOnly);
                if (setupExes.Length > 0) return setupExes[0];

                var uninstExes = Directory.GetFiles(setupDir, "uninstall*.exe", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(setupDir, "uninst*.exe", SearchOption.TopDirectoryOnly))
                    .ToArray();
                if (uninstExes.Length > 0) return uninstExes[0];
            }
        }
        catch { }

        // Рівень 1: підпапки
        try
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "x64", "x86", "drivers", "lang", "languages", "resources", "locales", "icons" };

            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (skip.Contains(Path.GetFileName(sub))) continue;

                var subFiles = new List<string>();
                try
                {
                    subFiles.AddRange(Directory.GetFiles(sub, "uninstall*.exe", SearchOption.TopDirectoryOnly));
                    subFiles.AddRange(Directory.GetFiles(sub, "uninst*.exe",    SearchOption.TopDirectoryOnly));
                    subFiles.AddRange(Directory.GetFiles(sub, "inins*.exe",     SearchOption.TopDirectoryOnly));
                }
                catch { }
                if (subFiles.Count > 0) return subFiles[0];

                // setup/ в підпапці
                try
                {
                    var subSetup = Path.Combine(sub, "setup");
                    if (Directory.Exists(subSetup))
                    {
                        var instup = Path.Combine(subSetup, "instup.exe");
                        if (File.Exists(instup)) return instup;
                    }
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static Process? TryLaunchProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            var proc = Process.Start(psi);
            Logger.Info($"[AVDetect] Launched: {fileName} {args} → PID={proc?.Id}");
            return proc;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AVDetect] Launch error: {ex.Message}");
            // Якщо не вдалось — відкриваємо список програм
            try { return Process.Start(new ProcessStartInfo("control.exe", "appwiz.cpl") { UseShellExecute = true }); } catch { }
            return null;
        }
    }

    private static string RemoveSilentFlags(string args)
    {
        // Прибираємо флаги тихого видалення — хочемо щоб юзер бачив UI
        var silentFlags = new[] { "/S", "/s", "/silent", "/SILENT", "/quiet", "/QUIET", "/q", "/Q", "/unattended" };
        var result = args;
        foreach (var flag in silentFlags)
        {
            result = result.Replace($" {flag}", " ", StringComparison.OrdinalIgnoreCase).Trim();
        }
        return result.Trim();
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

                    var brand = ExtractBrand(displayName);
                    if (brand == null) continue;

                    if (!found.ContainsKey(brand))
                    {
                        found[brand] = new DetectedAntivirus
                        {
                            Name = brand,
                            DisplayName = displayName,
                        };
                    }
                    // Якщо вже є — не перезаписуємо (registry дасть більше даних)
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
        // Явно читаємо обидва views: 64-bit і 32-bit (WOW64).
        // КРИТИЧНО: наш процес win-x86 (32-bit), тому Registry.LocalMachine автоматично
        // редіректить SOFTWARE\... → SOFTWARE\WOW6432Node\... — 64-bit AV (Avast, Kaspersky тощо)
        // пишуть UninstallString тільки в 64-bit registry і без RegistryView.Registry64 ми їх не бачимо.
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        var uninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var view in views)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hklm.OpenSubKey(uninstallSubKey);
                if (root == null) continue;

                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        if (sub == null) continue;

                        var displayName = sub.GetValue("DisplayName")?.ToString() ?? "";
                        if (string.IsNullOrEmpty(displayName)) continue;

                        var brand = ExtractBrand(displayName);
                        if (brand == null) continue;

                        var uninstallStr = sub.GetValue("UninstallString")?.ToString() ?? "";
                        var installLoc   = sub.GetValue("InstallLocation")?.ToString() ?? "";
                        var viewLabel    = view == RegistryView.Registry64 ? "x64" : "x32";
                        var fullRegKey   = $@"{viewLabel}\{uninstallSubKey}\{subName}";

                        if (!found.ContainsKey(brand))
                        {
                            found[brand] = new DetectedAntivirus
                            {
                                Name           = brand,
                                DisplayName    = displayName,
                                UninstallCommand = uninstallStr,
                                InstallFolder  = installLoc,
                                RegistryKey    = fullRegKey,
                            };
                        }
                        else
                        {
                            var existing = found[brand];
                            if (displayName.Length > existing.DisplayName.Length)
                                existing.DisplayName = displayName;
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
}
