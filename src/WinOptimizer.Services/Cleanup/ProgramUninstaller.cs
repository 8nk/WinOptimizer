using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Деінсталяція всіх не-системних програм.
/// </summary>
public static class ProgramUninstaller
{
    // Програми які НЕ МОЖНА видаляти (системні компоненти Windows)
    private static readonly string[] ProtectedKeywords =
    {
        "Microsoft Visual C++", "Microsoft .NET", ".NET Framework", ".NET Runtime",
        "Windows Driver", "Microsoft Windows", "Windows SDK",
        "Microsoft Edge", "Microsoft OneDrive", "Microsoft Update",
        "NVIDIA", "AMD ", "Realtek", "Intel ",
        "WinOptimizer", "Windows Defender"
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
                    var success = await UninstallProgramAsync(prog);
                    if (success)
                    {
                        removed.Add(prog.Name);
                        Logger.Info($"OK: Uninstalled {prog.Name}");
                    }
                    else
                    {
                        Logger.Warn($"FAIL: Could not uninstall {prog.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ERROR uninstalling {prog.Name}: {ex.Message}");
                }
            }

            // Also remove UWP/Store apps (except system)
            onProgress?.Invoke("Видалення UWP додатків...");
            var uwpRemoved = await RemoveUwpAppsAsync(token);
            removed.AddRange(uwpRemoved);
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

    private static async Task<List<ProgramInfo>> GetInstalledProgramsAsync()
    {
        var programs = new List<ProgramInfo>();

        // CRITICAL FIX: Використовуємо -EncodedCommand замість -Command
        // щоб уникнути конфлікту лапок в PowerShell.
        // Старий код ламався: внутрішні " закривали зовнішню " в Arguments.
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

    private static async Task<bool> UninstallProgramAsync(ProgramInfo prog)
    {
        // Use quiet uninstall if available
        var cmd = !string.IsNullOrEmpty(prog.QuietUninstallString)
            ? prog.QuietUninstallString
            : prog.UninstallString;

        Logger.Info($"  Uninstall command: [{cmd}]");

        // MSI uninstall
        if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            // Extract product code {GUID}
            var guidStart = cmd.IndexOf('{');
            var guidEnd = cmd.IndexOf('}');
            if (guidStart >= 0 && guidEnd > guidStart)
            {
                var guid = cmd[guidStart..(guidEnd + 1)];
                Logger.Info($"  MSI uninstall: {guid}");
                return await RunProcessAsync("msiexec.exe", $"/x {guid} /qn /norestart", 120);
            }
            // Fallback: modify the command
            cmd = cmd.Replace("/I", "/X").Replace("/i", "/x");
            if (!cmd.Contains("/qn")) cmd += " /qn /norestart";
            return await RunPsUninstallAsync(cmd, 120);
        }

        // EXE uninstall - add silent flags
        if (!cmd.Contains("/S", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Contains("/silent", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Contains("/quiet", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Contains("/VERYSILENT", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Contains("/qn", StringComparison.OrdinalIgnoreCase))
        {
            cmd += " /S /VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
        }

        return await RunPsUninstallAsync(cmd, 90);
    }

    private static async Task<bool> RunProcessAsync(string fileName, string arguments, int timeoutSec)
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
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                Logger.Info($"  Process exit code: {proc.ExitCode}");
                return proc.ExitCode == 0 || proc.ExitCode == 3010; // 3010 = reboot required
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Logger.Warn($"  Process timed out after {timeoutSec}s");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"  RunProcess error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run uninstall via PowerShell Start-Process — handles paths with spaces/quotes properly.
    /// </summary>
    private static async Task<bool> RunPsUninstallAsync(string command, int timeoutSec)
    {
        try
        {
            // Use PowerShell to properly execute the uninstall command
            // This handles paths with spaces and quotes much better than cmd.exe
            var psCmd = $"Start-Process -FilePath cmd.exe -ArgumentList '/c {command.Replace("'", "''")}' " +
                        $"-Wait -NoNewWindow -PassThru | ForEach-Object {{ $_.ExitCode }}";

            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"{psCmd}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            try
            {
                var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                Logger.Info($"  PS uninstall output: [{output.Trim()}]");

                if (int.TryParse(output.Trim(), out var exitCode))
                    return exitCode == 0 || exitCode == 3010;

                return proc.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Logger.Warn($"  PS uninstall timed out after {timeoutSec}s");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"  PS uninstall error: {ex.Message}");
            return false;
        }
    }

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

    private static async Task<string> RunPsEncodedAsync(string encodedCommand)
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
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
    }

    private static async Task<string> RunPsAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellHelper.Path,
            Arguments = $"-NoProfile -NoLogo -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
    }

    private class ProgramInfo
    {
        public string Name { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string QuietUninstallString { get; set; } = "";
    }
}
