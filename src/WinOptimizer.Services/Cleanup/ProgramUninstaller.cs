using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// АГРЕСИВНА деінсталяція програм — БЕЗ ДІАЛОГІВ!
///
/// Стратегія:
/// 1. Вбити процес програми
/// 2. Спробувати тихий uninstall (15с timeout)
/// 3. Якщо з'являється вікно — вбити uninstaller
/// 4. Примусово видалити папку програми
/// 5. Почистити registry
///
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// </summary>
public static class ProgramUninstaller
{
    // Програми які НЕ МОЖНА видаляти (системні компоненти Windows)
    private static readonly string[] ProtectedKeywords =
    {
        "Microsoft Visual C++", "Microsoft .NET", ".NET Framework", ".NET Runtime",
        "Windows Driver", "Microsoft Windows", "Windows SDK",
        "Microsoft Edge", "Microsoft OneDrive", "Microsoft Update",
        "NVIDIA Graphics", "NVIDIA PhysX", "NVIDIA GeForce",
        "AMD Software", "AMD Chipset", "AMD Radeon",
        "Realtek", "Intel(R)", "Intel ",
        "WinOptimizer", "Windows Defender",
        "Vulkan Runtime", "Microsoft XNA",
        "DirectX", "OpenAL",
    };

    public static async Task<List<string>> UninstallAllProgramsAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        var removed = new List<string>();

        try
        {
            onProgress?.Invoke("Отримання списку програм...");
            var programs = await GetInstalledProgramsAsync();
            Logger.Info($"Found {programs.Count} installed programs");

            foreach (var p in programs)
                Logger.Info($"  Program: [{p.Name}] Uninstall: [{p.UninstallString}]");

            var toRemove = programs
                .Where(p => !IsProtected(p.Name))
                .ToList();

            Logger.Info($"Will attempt to uninstall {toRemove.Count} programs");

            for (int i = 0; i < toRemove.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var prog = toRemove[i];
                onProgress?.Invoke($"Видалення {prog.Name}... ({i + 1}/{toRemove.Count})");
                Logger.Info($"Uninstalling: {prog.Name}");

                try
                {
                    // КРОК 1: Вбити процес програми (якщо запущена)
                    KillProgramProcess(prog);

                    // КРОК 2: Спробувати тихе видалення (15с timeout!)
                    var success = await SilentUninstallAsync(prog);

                    if (success)
                    {
                        removed.Add(prog.Name);
                        Logger.Info($"OK: Uninstalled {prog.Name}");
                    }
                    else
                    {
                        // КРОК 3: Тихе видалення не вдалось — примусово видаляємо папку
                        Logger.Info($"Silent uninstall failed for {prog.Name} — force deleting folder");
                        ForceDeleteProgramFolder(prog);
                        removed.Add($"{prog.Name} (force)");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ERROR uninstalling {prog.Name}: {ex.Message}");
                }
            }

            // UWP/Store apps (тільки Win10+)
            if (IsWindows10OrLater())
            {
                onProgress?.Invoke("Видалення UWP додатків...");
                var uwpRemoved = await RemoveUwpAppsAsync(token);
                removed.AddRange(uwpRemoved);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"ProgramUninstaller error: {ex.Message}");
        }

        return removed;
    }

    private static bool IsProtected(string name)
    {
        return ProtectedKeywords.Any(k =>
            name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Вбити процес програми перед видаленням.
    /// </summary>
    private static void KillProgramProcess(ProgramInfo prog)
    {
        try
        {
            // Витягнути exe path з uninstall string
            var exePath = ExtractExePath(prog.UninstallString);
            if (string.IsNullOrEmpty(exePath)) return;

            var installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(installDir)) return;

            // Вбити ВСІ процеси які запущені з цієї папки
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path != null && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(true);
                        Logger.Info($"  Killed: {proc.ProcessName} (PID {proc.Id})");
                    }
                }
                catch { }
            }

            // Також вбити за назвою програми
            var progName = Path.GetFileNameWithoutExtension(exePath);
            try
            {
                foreach (var proc in Process.GetProcessesByName(progName))
                {
                    try { proc.Kill(true); } catch { }
                }
            }
            catch { }

            Thread.Sleep(500); // Почекати щоб файли звільнились
        }
        catch { }
    }

    /// <summary>
    /// Тихе видалення з КОРОТКИМ timeout.
    /// Якщо діалог вилазить — ми вбиваємо процес.
    /// </summary>
    private static async Task<bool> SilentUninstallAsync(ProgramInfo prog)
    {
        // Пріоритет: QuietUninstallString → MSI → EXE з silent flags
        var cmd = !string.IsNullOrEmpty(prog.QuietUninstallString)
            ? prog.QuietUninstallString
            : prog.UninstallString;

        Logger.Info($"  Uninstall command: [{cmd}]");

        // MSI uninstall — найнадійніший тихий метод
        if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var guidStart = cmd.IndexOf('{');
            var guidEnd = cmd.IndexOf('}');
            if (guidStart >= 0 && guidEnd > guidStart)
            {
                var guid = cmd[guidStart..(guidEnd + 1)];
                Logger.Info($"  MSI uninstall: {guid}");
                return await RunSilentProcessAsync("msiexec.exe", $"/x {guid} /qn /norestart", 30);
            }
            cmd = cmd.Replace("/I", "/X").Replace("/i", "/x");
            if (!cmd.Contains("/qn")) cmd += " /qn /norestart";
            return await RunSilentCmdAsync(cmd, 30);
        }

        // EXE uninstall — додаємо ВСІ можливі silent flags
        var silentCmd = cmd;
        if (!HasAnySilentFlag(silentCmd))
        {
            // Спробувати з усіма відомими silent flags
            silentCmd += " /S /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /quiet /qn --silent --uninstall";
        }

        // КОРОТКИЙ timeout — 15с! Якщо діалог з'явиться, вбиваємо
        return await RunSilentCmdAsync(silentCmd, 15);
    }

    private static bool HasAnySilentFlag(string cmd)
    {
        var flags = new[] { "/S", "/silent", "/quiet", "/VERYSILENT", "/qn", "--silent", "--quiet", "-s" };
        return flags.Any(f => cmd.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Запустити процес з моніторингом вікон.
    /// Якщо з'являється вікно — ВБИТИ негайно (діалог!).
    /// </summary>
    private static async Task<bool> RunSilentProcessAsync(string fileName, string arguments, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // Моніторимо: якщо процес створює вікно — вбити!
            var startTime = DateTime.Now;
            while (!proc.HasExited)
            {
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                if (elapsed > timeoutSec)
                {
                    Logger.Info($"  Timeout {timeoutSec}s — killing");
                    try { proc.Kill(true); } catch { }
                    return false;
                }

                // Перевірити чи є видиме вікно (= діалог!)
                try
                {
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero && elapsed > 3)
                    {
                        // Вікно з'явилось через 3+ секунди = діалог видалення!
                        Logger.Info($"  Dialog detected — killing uninstaller!");
                        try { proc.Kill(true); } catch { }
                        return false;
                    }
                }
                catch { }

                await Task.Delay(500);
            }

            Logger.Info($"  Exit code: {proc.ExitCode}");
            return proc.ExitCode == 0 || proc.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            Logger.Warn($"  Silent process error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Запустити команду через cmd з моніторингом.
    /// </summary>
    private static async Task<bool> RunSilentCmdAsync(string command, int timeoutSec)
    {
        try
        {
            // Запускаємо через cmd
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var startTime = DateTime.Now;
            while (!proc.HasExited)
            {
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                if (elapsed > timeoutSec)
                {
                    Logger.Info($"  CMD timeout {timeoutSec}s — killing all");
                    KillProcessTree(proc);
                    return false;
                }

                // Шукаємо дочірні процеси з вікнами (= діалоги!)
                if (elapsed > 3)
                {
                    try
                    {
                        // Шукаємо вікна всіх процесів які з'явились після запуску
                        foreach (var childProc in Process.GetProcesses())
                        {
                            try
                            {
                                if (childProc.StartTime > startTime.AddSeconds(-1) &&
                                    childProc.MainWindowHandle != IntPtr.Zero &&
                                    childProc.Id != proc.Id &&
                                    !childProc.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
                                    !childProc.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase))
                                {
                                    var title = childProc.MainWindowTitle;
                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        Logger.Info($"  Dialog window found: [{title}] — killing!");
                                        try { childProc.Kill(true); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                await Task.Delay(500);
            }

            Logger.Info($"  CMD exit code: {proc.ExitCode}");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"  Silent cmd error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Примусово видалити папку програми якщо тихе видалення не вдалось.
    /// </summary>
    private static void ForceDeleteProgramFolder(ProgramInfo prog)
    {
        try
        {
            var exePath = ExtractExePath(prog.UninstallString);
            if (string.IsNullOrEmpty(exePath)) return;

            var installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return;

            // Не видаляємо системні папки!
            var lower = installDir.ToLowerInvariant();
            if (lower.Contains(@"\windows\") || lower == @"c:\windows" ||
                lower == @"c:\program files" || lower == @"c:\program files (x86)")
                return;

            Logger.Info($"  Force deleting folder: {installDir}");

            // Спочатку вбити всі процеси з цієї папки
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path != null && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(true);
                    }
                }
                catch { }
            }

            Thread.Sleep(500);

            // Видалити папку через rd /s /q
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c rd /s /q \"{installDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(15000);
            }
            catch { }

            // Також спробувати через .NET
            if (Directory.Exists(installDir))
            {
                try { Directory.Delete(installDir, true); } catch { }
            }

            Logger.Info($"  Folder deleted: {!Directory.Exists(installDir)}");
        }
        catch (Exception ex)
        {
            Logger.Info($"  Force delete error: {ex.Message}");
        }
    }

    /// <summary>
    /// Витягнути шлях до exe з uninstall string.
    /// </summary>
    private static string? ExtractExePath(string uninstallString)
    {
        if (string.IsNullOrEmpty(uninstallString)) return null;

        // "C:\path\uninstall.exe" /flags → C:\path\uninstall.exe
        if (uninstallString.StartsWith('"'))
        {
            var endQuote = uninstallString.IndexOf('"', 1);
            if (endQuote > 1)
                return uninstallString[1..endQuote];
        }

        // C:\path\uninstall.exe /flags → C:\path\uninstall.exe
        var parts = uninstallString.Split(' ');
        foreach (var part in parts)
        {
            if (part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathRooted(part))
                return part;
        }

        return null;
    }

    private static void KillProcessTree(Process proc)
    {
        try { proc.Kill(true); } catch { }
    }

    // ================================================================
    // GET INSTALLED PROGRAMS (Win7+ compatible)
    // ================================================================

    private static async Task<List<ProgramInfo>> GetInstalledProgramsAsync()
    {
        var programs = new List<ProgramInfo>();

        var psScript =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "$paths = @(\n" +
            "  'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',\n" +
            "  'HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',\n" +
            "  'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'\n" +
            ")\n" +
            "Get-ItemProperty $paths -ErrorAction SilentlyContinue |\n" +
            "  Where-Object { $_.DisplayName -and $_.UninstallString } |\n" +
            "  ForEach-Object {\n" +
            "    $n = $_.DisplayName -replace '\\t', ' '\n" +
            "    $u = $_.UninstallString -replace '\\t', ' '\n" +
            "    $q = if ($_.QuietUninstallString) { $_.QuietUninstallString -replace '\\t', ' ' } else { '' }\n" +
            "    \"$n`t$u`t$q\"\n" +
            "  }";

        var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        var output = await RunPsEncodedAsync(encodedCmd);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                programs.Add(new ProgramInfo
                {
                    Name = parts[0].Trim(),
                    UninstallString = parts[1].Trim(),
                    QuietUninstallString = parts.Length > 2 ? parts[2].Trim() : ""
                });
            }
        }

        return programs;
    }

    // ================================================================
    // UWP APPS (Win10+ only)
    // ================================================================

    private static async Task<List<string>> RemoveUwpAppsAsync(CancellationToken token)
    {
        var removed = new List<string>();

        try
        {
            var psScript =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                "$protected = @(\n" +
                "  'Microsoft.WindowsStore',\n" +
                "  'Microsoft.DesktopAppInstaller',\n" +
                "  'Microsoft.WindowsTerminal',\n" +
                "  'Microsoft.Windows.Photos',\n" +
                "  'Microsoft.WindowsCalculator',\n" +
                "  'Microsoft.MSPaint',\n" +
                "  'Microsoft.WindowsNotepad',\n" +
                "  'Microsoft.SecHealthUI'\n" +
                ")\n" +
                "Get-AppxPackage -AllUsers | Where-Object {\n" +
                "  $_.IsFramework -eq $false -and\n" +
                "  $_.SignatureKind -eq 'Store' -and\n" +
                "  $protected -notcontains $_.Name\n" +
                "} | ForEach-Object {\n" +
                "  try {\n" +
                "    Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop\n" +
                "    $_.Name\n" +
                "  } catch { }\n" +
                "}";

            var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var output = await RunPsEncodedAsync(encodedCmd);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    removed.Add($"UWP: {name}");
                    Logger.Info($"Removed UWP: {name}");
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"UWP removal error: {ex.Message}"); }

        return removed;
    }

    // ================================================================
    // WINDOWS VERSION CHECK
    // ================================================================

    private static bool IsWindows10OrLater()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            return version.Major >= 10; // Win10 = 10.0, Win11 = 10.0 (build 22000+)
        }
        catch { return false; }
    }

    // ================================================================
    // POWERSHELL HELPERS
    // ================================================================

    private static async Task<string> RunPsEncodedAsync(string encodedCommand)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -NoLogo -EncodedCommand {encodedCommand}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";

            using var cts = new CancellationTokenSource(60000); // 60s max for listing
            try
            {
                var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                return output;
            }
            catch
            {
                try { proc.Kill(true); } catch { }
                return "";
            }
        }
        catch { return ""; }
    }

    private class ProgramInfo
    {
        public string Name { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string QuietUninstallString { get; set; } = "";
    }
}
