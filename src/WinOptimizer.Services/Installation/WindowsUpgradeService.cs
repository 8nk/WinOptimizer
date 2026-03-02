using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Installation;

/// <summary>
/// Upgrade Windows через mounted ISO + setup.exe /unattend:autounattend.xml.
/// Consumer DVD setup.exe НЕ підтримує /auto, /quiet, /DynamicUpdate, /compat.
/// Upgrade режим задається через unattend.xml (<Upgrade>true</Upgrade>).
/// </summary>
public static class WindowsUpgradeService
{
    // Desktop лог
    private static readonly string DesktopLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        "WinOptimizer_Deploy.log");
    private static void DLog(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [UPGRADE] {msg}";
        Logger.Info($"[UPGRADE] {msg}");
        try { File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
    }

    /// <summary>
    /// Виконати upgrade Windows.
    /// 1. Bypass TPM/CPU requirements (для Win 11 на старому залізі)
    /// 2. Mount ISO
    /// 3. Запустити setup.exe /auto upgrade
    /// 4. Система перезавантажиться автоматично
    /// </summary>
    /// <param name="isoPath">Шлях до завантаженого ISO</param>
    /// <param name="targetVersion">Цільова версія: "10" або "11"</param>
    /// <param name="onDetail">Callback деталей для UI</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>true якщо upgrade запущено успішно</returns>
    public static async Task<bool> StartUpgradeAsync(
        string isoPath,
        string targetVersion,
        Action<string>? onDetail = null,
        CancellationToken ct = default)
    {
        DLog($"=== Starting Windows upgrade ===");
        DLog($"ISO: {isoPath}");
        DLog($"Target version: Windows {targetVersion}");

        if (!File.Exists(isoPath))
        {
            DLog($"ISO file not found: {isoPath}");
            throw new FileNotFoundException("ISO file not found", isoPath);
        }

        // Step 1: Bypass TPM/CPU/HW requirements (для будь-якої версії — на всяк випадок)
        onDetail?.Invoke("Налаштування сумісності...");
        await BypassTpmRequirementsAsync();

        // Step 2: Mount ISO
        onDetail?.Invoke("Монтування ISO образу...");
        var mountedDrive = await MountIsoAsync(isoPath, ct);
        if (string.IsNullOrEmpty(mountedDrive))
        {
            DLog("Failed to mount ISO");
            throw new Exception("Failed to mount ISO image");
        }
        DLog($"ISO mounted at: {mountedDrive}");

        // Step 3: Verify setup.exe exists
        var setupExe = Path.Combine(mountedDrive, "setup.exe");
        if (!File.Exists(setupExe))
        {
            DLog($"setup.exe not found at: {setupExe}");
            await DismountIsoAsync(isoPath);
            throw new Exception($"setup.exe not found on mounted ISO ({mountedDrive})");
        }
        DLog($"setup.exe found: {setupExe}");

        // Step 3.5: Validate ISO — check editions and compatibility
        onDetail?.Invoke("Перевірка сумісності ISO...");
        await ValidateIsoContentsAsync(mountedDrive);

        // Step 4: Launch setup.exe
        // Стратегія: спочатку /auto upgrade (тихий режим), якщо не підтримується — fallback на GUI
        onDetail?.Invoke("Запуск автоматичної установки Windows...");

        try
        {
            // Крок 1: Очищаємо C:\$WINDOWS.~BT від попередніх спроб (error 183 fix)
            try
            {
                var winBtPath = @"C:\$WINDOWS.~BT";
                if (Directory.Exists(winBtPath))
                {
                    DLog($"Cleaning up previous setup remnants: {winBtPath}");
                    var cleanScript = $@"
                        $p = '{winBtPath}'
                        if (Test-Path $p) {{
                            # Kill any setup processes that might hold locks
                            Get-Process -Name 'setup','SetupHost','setupprep','SetupPrep' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                            Start-Sleep -Seconds 2
                            # Try to remove the directory
                            Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue
                            # Also clean $WINDOWS.~WS if exists
                            $ws = 'C:\$WINDOWS.~WS'
                            if (Test-Path $ws) {{ Remove-Item -Path $ws -Recurse -Force -ErrorAction SilentlyContinue }}
                        }}
                    ";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cleanScript.Replace("\"", "\\\"")}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    var cleanProc = Process.Start(psi);
                    cleanProc?.WaitForExit(15000);
                    DLog($"Cleanup result: directory exists={Directory.Exists(winBtPath)}");
                }
            }
            catch (Exception ex)
            {
                DLog($"Cleanup warning: {ex.Message}");
            }

            // ================================================================
            // СТРАТЕГІЯ ЗАПУСКУ (2 спроби):
            // 1. setup.exe /auto upgrade (швидкий тест — Consumer DVD не підтримує)
            // 2. GUI mode + AutoClicker (основний метод для Consumer DVD)
            // ================================================================

            Process? upgradeProcess = null;
            Process? clickerProc = null;
            var autoArgs = "/auto upgrade /Telemetry Disable /DynamicUpdate Disable /Compat IgnoreWarning /MigrateDrivers All /ShowOOBE None";

            // === СПРОБА 1: setup.exe /auto upgrade (швидкий тест) ===
            // Consumer DVD НЕ підтримує /auto — але тестуємо на випадок VLK ISO
            DLog("=== TRY 1: setup.exe /auto upgrade (quick test) ===");
            onDetail?.Invoke("Перевірка тихої установки...");
            upgradeProcess = LaunchSetup(setupExe, autoArgs);
            if (upgradeProcess != null)
            {
                DLog($"setup.exe /auto started, PID: {upgradeProcess.Id}");
                await Task.Delay(5000, ct); // 5s — швидка перевірка

                if (!upgradeProcess.HasExited)
                {
                    DLog("✅ TRY 1 WORKING! setup.exe /auto upgrade running.");
                    onDetail?.Invoke("Тиха установка Windows працює...");
                }
                else
                {
                    DLog($"❌ TRY 1 failed: exit code {upgradeProcess.ExitCode} (0x{upgradeProcess.ExitCode:X8})");
                    DLog("Consumer DVD ISO does not support /auto upgrade — switching to GUI mode");
                    upgradeProcess = null;
                }
            }

            // === СПРОБА 2: GUI mode + AutoClicker ===
            if (upgradeProcess == null)
            {
                DLog("=== TRY 2: GUI mode + AutoClicker ===");
                onDetail?.Invoke("Запуск установки Windows (графічний режим)...");

                // Запускаємо AutoClicker ПЕРЕД setup.exe
                var autoClickerPath = await WriteAutoClickerScriptAsync();
                if (!string.IsNullOrEmpty(autoClickerPath))
                {
                    DLog($"Starting AutoClicker: {autoClickerPath}");
                    try
                    {
                        var clickerPsi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{autoClickerPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        clickerProc = Process.Start(clickerPsi);
                        DLog($"AutoClicker PID: {clickerProc?.Id}");
                        await Task.Delay(3000, ct);

                        if (clickerProc?.HasExited == true)
                        {
                            DLog($"⚠️ AutoClicker exited with code: {clickerProc.ExitCode}, retrying...");
                            clickerPsi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{autoClickerPath}\"";
                            clickerProc = Process.Start(clickerPsi);
                            DLog($"AutoClicker retry PID: {clickerProc?.Id}");
                            await Task.Delay(2000, ct);
                        }
                    }
                    catch (Exception ex) { DLog($"AutoClicker start failed: {ex.Message}"); }
                }

                // Запускаємо setup.exe БЕЗ параметрів
                DLog($"Launching: \"{setupExe}\" (no params — GUI mode)");
                upgradeProcess = LaunchSetup(setupExe, "");

                if (upgradeProcess != null)
                {
                    DLog($"setup.exe GUI started, PID: {upgradeProcess.Id}");
                    await Task.Delay(15000, ct);
                }
            }

            onDetail?.Invoke("Windows Setup запущено...");

            // Перевіряємо стан AutoClicker
            if (clickerProc != null)
            {
                if (clickerProc.HasExited)
                    DLog($"AutoClicker finished, exit code: {clickerProc.ExitCode}");
                else
                    DLog($"AutoClicker still running (PID: {clickerProc.Id})");
            }

            if (upgradeProcess == null)
            {
                DLog("Failed to start setup.exe");
                throw new Exception("Failed to start Windows Setup");
            }

            if (upgradeProcess.HasExited)
            {
                var exitCode = upgradeProcess.ExitCode;
                DLog($"setup.exe exited with code: {exitCode} (0x{exitCode:X8})");

                if (exitCode != 0)
                {
                    var errorMsg = exitCode switch
                    {
                        unchecked((int)0xC1900101) => "Driver compatibility issue",
                        unchecked((int)0xC1900200) => "System requirements not met",
                        unchecked((int)0x80070005) => "Access denied — run as administrator",
                        unchecked((int)0x800704DD) => "User refused elevation",
                        _ => $"Exit code: 0x{exitCode:X8}"
                    };
                    DLog($"❌ Setup FAILED: {errorMsg}");
                    await ReadSetupLogAsync();
                    throw new Exception($"Windows Setup failed: {errorMsg}");
                }
            }

            DLog("✅ Windows Setup is running");
            onDetail?.Invoke("Установка Windows працює...");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DLog($"Setup launch error: {ex.Message}");
            // Спробуємо dismount ISO
            try { await DismountIsoAsync(isoPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Bypass TPM 2.0 та CPU requirements для Windows 11.
    /// Додає registry ключі які дозволяють upgrade на непідтримуваному залізі.
    /// </summary>
    private static async Task BypassTpmRequirementsAsync()
    {
        DLog("Applying TPM/CPU bypass for Windows 11...");

        var script = @"
            # Allow upgrade with unsupported TPM or CPU
            $mosetupPath = 'HKLM:\SYSTEM\Setup\MoSetup'
            if (!(Test-Path $mosetupPath)) {
                New-Item -Path $mosetupPath -Force | Out-Null
            }
            Set-ItemProperty -Path $mosetupPath -Name 'AllowUpgradesWithUnsupportedTPMOrCPU' -Value 1 -Type DWord -Force

            # LabConfig bypass (for clean install scenarios)
            $labConfigPath = 'HKLM:\SYSTEM\Setup\LabConfig'
            if (!(Test-Path $labConfigPath)) {
                New-Item -Path $labConfigPath -Force | Out-Null
            }
            Set-ItemProperty -Path $labConfigPath -Name 'BypassTPMCheck' -Value 1 -Type DWord -Force
            Set-ItemProperty -Path $labConfigPath -Name 'BypassSecureBootCheck' -Value 1 -Type DWord -Force
            Set-ItemProperty -Path $labConfigPath -Name 'BypassRAMCheck' -Value 1 -Type DWord -Force
            Set-ItemProperty -Path $labConfigPath -Name 'BypassStorageCheck' -Value 1 -Type DWord -Force
            Set-ItemProperty -Path $labConfigPath -Name 'BypassCPUCheck' -Value 1 -Type DWord -Force

            Write-Output 'TPM/CPU bypass applied successfully'
        ";

        var result = await RunPowerShellAsync(script);
        DLog($"TPM bypass result: {result}");
    }

    /// <summary>
    /// Mount ISO через PowerShell Mount-DiskImage.
    /// </summary>
    /// <returns>Drive letter with backslash (e.g., "E:\")</returns>
    private static async Task<string> MountIsoAsync(string isoPath, CancellationToken ct)
    {
        DLog($"Mounting ISO: {isoPath}");

        var script = $@"
            $result = Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -PassThru
            $volume = $result | Get-Volume
            if ($volume) {{
                Write-Output ($volume.DriveLetter + ':\')
            }} else {{
                # Fallback: get drive letter via disk image
                $diskImage = Get-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'
                $driveLetter = ($diskImage | Get-Volume).DriveLetter
                if ($driveLetter) {{
                    Write-Output ($driveLetter + ':\')
                }} else {{
                    Write-Error 'Failed to get mounted drive letter'
                }}
            }}
        ";

        var result = await RunPowerShellAsync(script);
        var drive = result?.Trim();

        if (!string.IsNullOrEmpty(drive) && drive.Length >= 2 && drive[1] == ':')
        {
            DLog($"ISO mounted successfully at: {drive}");
            return drive;
        }

        DLog($"Mount failed, output: {result}");
        return "";
    }

    /// <summary>
    /// Dismount ISO.
    /// </summary>
    private static async Task DismountIsoAsync(string isoPath)
    {
        try
        {
            var script = $"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'";
            await RunPowerShellAsync(script);
            DLog("ISO dismounted");
        }
        catch (Exception ex)
        {
            DLog($"Dismount failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Create autounattend.xml for silent in-place upgrade.
    /// Цей файл забезпечує:
    /// - Автоматичне прийняття ліцензії
    /// - Skip OOBE
    /// - Зберегти файли та налаштування (in-place upgrade)
    /// </summary>
    private static async Task<string> CreateUpgradeUnattendAsync(string mountedDrive)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinOptimizer", "Data");
        Directory.CreateDirectory(dataDir);

        var unattendPath = Path.Combine(dataDir, "autounattend.xml");

        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
    <settings pass=""windowsPE"">
        <component name=""Microsoft-Windows-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"" xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
            <UserData>
                <AcceptEula>true</AcceptEula>
            </UserData>
            <UpgradeData>
                <Upgrade>true</Upgrade>
                <WillShowUI>Never</WillShowUI>
            </UpgradeData>
            <ComplianceCheck>
                <DisplayReport>Never</DisplayReport>
            </ComplianceCheck>
        </component>
    </settings>
    <settings pass=""oobeSystem"">
        <component name=""Microsoft-Windows-Shell-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"" xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
            <OOBE>
                <HideEULAPage>true</HideEULAPage>
                <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                <ProtectYourPC>3</ProtectYourPC>
                <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                <HideLocalAccountScreen>true</HideLocalAccountScreen>
            </OOBE>
        </component>
    </settings>
</unattend>";

        await File.WriteAllTextAsync(unattendPath, xml);
        DLog($"Autounattend created: {unattendPath}");
        return unattendPath;
    }

    /// <summary>
    /// Отримати поточну версію Windows (для логування).
    /// </summary>
    public static async Task<string> GetCurrentWindowsVersionAsync()
    {
        try
        {
            var script = "(Get-WmiObject Win32_OperatingSystem).Caption";
            var result = await RunPowerShellAsync(script);
            return result?.Trim() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Перевірити чи є достатньо місця для upgrade (мінімум 20 GB).
    /// </summary>
    public static bool HasEnoughSpace(long minimumBytes = 20L * 1024 * 1024 * 1024)
    {
        try
        {
            var cDrive = new DriveInfo("C");
            var freeSpace = cDrive.AvailableFreeSpace;
            DLog($"Free space on C: {freeSpace / 1024 / 1024} MB (need {minimumBytes / 1024 / 1024} MB)");
            return freeSpace >= minimumBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Прочитати лог Windows Setup для діагностики помилок.
    /// </summary>
    private static async Task ReadSetupLogAsync()
    {
        try
        {
            var logPaths = new[]
            {
                @"C:\$WINDOWS.~BT\Sources\Panther\setupact.log",
                @"C:\$WINDOWS.~BT\Sources\Panther\setuperr.log",
                @"C:\Windows\Panther\setupact.log",
                @"C:\Windows\Panther\setuperr.log"
            };

            foreach (var logPath in logPaths)
            {
                if (File.Exists(logPath))
                {
                    var lines = await File.ReadAllLinesAsync(logPath);
                    // Останні 20 рядків
                    var tail = lines.Skip(Math.Max(0, lines.Length - 20)).ToArray();
                    DLog($"--- {logPath} (last {tail.Length} lines) ---");
                    foreach (var line in tail)
                    {
                        DLog($"  {line}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DLog($"Cannot read setup logs: {ex.Message}");
        }
    }

    /// <summary>
    /// Запустити setup.exe з параметрами. Повертає Process або null при помилці.
    /// </summary>
    private static Process? LaunchSetup(string setupExe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false,
                // WorkingDirectory = папка з setup.exe (DLL_NOT_FOUND fix)
                WorkingDirectory = Path.GetDirectoryName(setupExe) ?? "",
            };
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            DLog($"LaunchSetup error ({args}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Копіює весь вміст директорії (рекурсивно).
    /// Використовується для копіювання ISO → локальну папку.
    /// </summary>
    private static async Task CopyDirectoryAsync(string sourceDir, string destDir,
        Action<string>? onDetail = null, CancellationToken ct = default)
    {
        var source = new DirectoryInfo(sourceDir);
        if (!source.Exists) throw new DirectoryNotFoundException($"Source not found: {sourceDir}");

        Directory.CreateDirectory(destDir);

        // Рахуємо файли для прогресу
        var allFiles = source.GetFiles("*", SearchOption.AllDirectories);
        var totalFiles = allFiles.Length;
        long totalBytes = allFiles.Sum(f => f.Length);
        long copiedBytes = 0;
        var copiedCount = 0;

        DLog($"Copying {totalFiles} files ({totalBytes / 1024 / 1024} MB)...");

        // Копіюємо файли
        foreach (var file in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDir, file.FullName);
            var destFile = Path.Combine(destDir, relativePath);
            var destFileDir = Path.GetDirectoryName(destFile);
            if (destFileDir != null) Directory.CreateDirectory(destFileDir);

            // Копіюємо файл
            File.Copy(file.FullName, destFile, true);
            copiedBytes += file.Length;
            copiedCount++;

            // Прогрес кожні 50 файлів або великих файлів
            if (copiedCount % 50 == 0 || file.Length > 100_000_000)
            {
                var percent = totalBytes > 0 ? (int)(copiedBytes * 100 / totalBytes) : 0;
                var msg = $"Копіювання файлів: {percent}% ({copiedBytes / 1024 / 1024} MB)";
                onDetail?.Invoke(msg);
                DLog(msg);
                await Task.Delay(10, ct); // Yield для UI
            }
        }

        DLog($"Copy complete: {copiedCount} files, {copiedBytes / 1024 / 1024} MB");
    }

    /// <summary>
    /// Спробувати запустити setup.exe з параметрами і перевірити чи він працює.
    /// Повертає true якщо setup.exe запустився і продовжує працювати (або запустив субпроцес).
    /// Повертає false якщо setup.exe впав або вийшов одразу (параметри не підтримуються).
    /// </summary>
    private static async Task<bool> TryLaunchSetupAsync(string setupExe, string args, CancellationToken ct)
    {
        DLog($"TryLaunch: \"{setupExe}\" {args}");

        var process = LaunchSetup(setupExe, args);
        if (process == null)
        {
            DLog("  → LaunchSetup returned null (failed to start)");
            return false;
        }

        DLog($"  → PID: {process.Id}, waiting 15s...");
        await Task.Delay(15000, ct);

        // Якщо процес ще працює — все ок
        if (!process.HasExited)
        {
            DLog($"  → RUNNING after 15s — success!");
            return true;
        }

        var exitCode = process.ExitCode;
        DLog($"  → Exited with code: {exitCode} (0x{exitCode:X8})");

        // Exit code 0 — може запустив субпроцес
        if (exitCode == 0)
        {
            DLog("  → Exit 0, checking for setup subprocess...");
            await Task.Delay(5000, ct);

            var checkScript = "Get-Process -Name 'SetupHost','setupprep','SetupPrep' -ErrorAction SilentlyContinue | Select-Object Id,ProcessName | Format-Table -AutoSize";
            var checkResult = await RunPowerShellAsync(checkScript);
            DLog($"  → Setup processes: {checkResult.Trim()}");

            if (!string.IsNullOrWhiteSpace(checkResult) && checkResult.Contains("Setup"))
            {
                DLog("  → Subprocess found — success!");
                return true;
            }
        }

        // Спроба не вдалась — прочитаємо лог для діагностики
        DLog("  → FAILED, reading setup log...");
        await ReadSetupLogAsync();

        // Вбиваємо залишки якщо є
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Записує AutoClicker PowerShell скрипт на диск.
    /// v4: Чистий PowerShell — БЕЗ C# Add-Type компіляції!
    /// UI Automation (primary) + WScript.Shell AppActivate + SendKeys (fallback)
    /// </summary>
    private static async Task<string> WriteAutoClickerScriptAsync()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinOptimizer", "Data");
        Directory.CreateDirectory(dataDir);

        var scriptPath = Path.Combine(dataDir, "setup_autoclicker.ps1");

        // AutoClicker v4.2: Multilingual (EN + UK + RU) — NO type literals in functions!
        // UIA types stored in variables — avoids function compilation errors in PS 5.1
        // File written with UTF-8 BOM — ensures PS 5.1 reads encoding correctly
        var script = @"
# WinOptimizer Setup AutoClicker v4.1
# ZERO type literals in function bodies!
# UIA types stored in global variables to avoid PS 5.1 compilation errors

$ErrorActionPreference = 'Continue'

# === FIRST: setup error capture ===
$logFile = Join-Path $env:ProgramData 'WinOptimizer\Data\autoclicker.log'
$deskLog = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'WinOptimizer_AutoClicker.log'
$logDir = Split-Path $logFile -Parent
if (-not (Test-Path $logDir)) { New-Item -Path $logDir -ItemType Directory -Force | Out-Null }

# Clear old log
'' | Set-Content -Path $logFile -ErrorAction SilentlyContinue

function Log($msg) {
    $line = ""[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg""
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
    # Also write to Desktop for easy access
    Add-Content -Path $deskLog -Value $line -ErrorAction SilentlyContinue
}

# Global error trap — catches ANYTHING
trap {
    Log ""TRAP ERROR: $_""
    Log ""Stack: $($_.ScriptStackTrace)""
    continue
}

Log '=========================================='
Log '=== AutoClicker v4.2 (Multilingual) ==='
Log ""PID: $PID""
Log ""PS Version: $($PSVersionTable.PSVersion)""
Log ""OS: $([Environment]::OSVersion.VersionString)""
Log ""Encoding: $([Console]::OutputEncoding.EncodingName)""
Log ""CodePage: $([Console]::OutputEncoding.CodePage)""
try {
    $uiCult = (Get-UICulture).Name
    $sysCult = (Get-WinSystemLocale).Name
    Log ""UI Culture: $uiCult""
    Log ""System Locale: $sysCult""
} catch { Log ""Culture detection failed: $_"" }

# Check elevation
try {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Log ""Elevated: $isAdmin""
} catch {
    Log ""Cannot check elevation: $_""
}

# === Load SendKeys (for fallback) ===
$sendKeysLoaded = $false
try {
    Add-Type -AssemblyName System.Windows.Forms
    $sendKeysLoaded = $true
    Log 'System.Windows.Forms: LOADED'
} catch {
    Log ""System.Windows.Forms FAILED: $_""
}

# === Create WScript.Shell (for AppActivate) ===
$wsh = $null
try {
    $wsh = New-Object -ComObject WScript.Shell
    Log 'WScript.Shell: CREATED'
} catch {
    Log ""WScript.Shell FAILED: $_""
}

# === Load UI Automation + store types in variables ===
$uiaLoaded = $false
$global:tAE = $null     # AutomationElement type
$global:tTS = $null     # TreeScope type
$global:tCT = $null     # ControlType type
$global:tPC = $null     # PropertyCondition type
$global:tIP = $null     # InvokePattern type
$global:tSIP = $null    # SelectionItemPattern type
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    # Store types in variables — NO type literals in functions!
    $global:tAE = [System.Windows.Automation.AutomationElement]
    $global:tTS = [System.Windows.Automation.TreeScope]
    $global:tCT = [System.Windows.Automation.ControlType]
    $global:tPC = [System.Windows.Automation.PropertyCondition]
    $global:tIP = [System.Windows.Automation.InvokePattern]
    $global:tSIP = [System.Windows.Automation.SelectionItemPattern]
    $uiaLoaded = $true
    Log 'UI Automation: LOADED (types stored in variables)'
} catch {
    Log ""UI Automation FAILED: $_""
}

# === Button texts (EN + UK + RU) ===
$targetButtons = @(
    # English
    'Next', 'Accept', 'Install', 'OK', 'Yes', 'Confirm', 'Continue',
    # Ukrainian
    'Далі', 'Прийняти', 'Встановити', 'Так', 'Підтвердити', 'Продовжити',
    # Russian
    'Далее', 'Принять', 'Установить', 'Да', 'Подтвердить', 'Продолжить',
    'Начать', 'Согласен'
)

# === Buttons to NEVER click ===
$excludeButtons = @(
    'Back', 'Cancel', 'No', 'Close', 'Minimize', 'Maximize',
    'Назад', 'Скасувати', 'Ні', 'Закрити', 'Згорнути',
    'Отмена', 'Отменить', 'Нет', 'Закрыть', 'Свернуть'
)

# === Radio buttons: select 'Keep files and apps' (best option first!) ===
$keepFileTexts = @(
    # BEST: Keep files AND apps (only works if ISO language matches system!)
    'Keep personal files and apps',
    'Зберегти особисті файли та програми',
    'Сохранить личные файлы и приложения',
    # OK: Keep personal files only
    'Keep personal files',
    'Зберегти особисті файли', 'Зберегти лише особисті файли',
    'Сохранить личные файлы', 'Сохранить только личные файлы',
    # Partial matches
    'Keep files', 'Зберегти файли', 'Сохранить файлы'
)

# === Window title patterns ===
$setupTitles = @(
    'Windows Setup', 'Windows 10', 'Windows 11', 'Setup',
    'Програма інсталяції', 'Інсталяція Windows',
    'Программа установки', 'Установка Windows'
)

# === Process names ===
$setupProcesses = @('setup', 'SetupHost', 'setupprep', 'SetupPrep')

# === Browser processes to EXCLUDE ===
$browserProcesses = @('msedge', 'chrome', 'firefox', 'iexplore', 'MicrosoftEdge', 'opera', 'brave')

# === Tab escalation state ===
$global:lastSetupPid = 0
$global:samePidStreak = 0

# ============================================================
# UIA click — all in ScriptBlock, NO type literals in outer scope
# ============================================================
$uiaClickBlock = {
    # This ScriptBlock is compiled ONLY when invoked
    # All type references resolve at invocation time (after Add-Type)
    $root = $global:tAE::RootElement
    $winCondition = New-Object ($global:tPC.FullName) @(
        $global:tAE::ControlTypeProperty,
        $global:tCT::Window
    )
    $windows = $root.FindAll($global:tTS::Children, $winCondition)

    $setupWin = $null
    foreach ($w in $windows) {
        try {
            $wName = $w.Current.Name
            $wPid = $w.Current.ProcessId

            # Get process info — to filter browsers
            $procName = ''
            try {
                $pp = Get-Process -Id $wPid -ErrorAction SilentlyContinue
                if ($pp) { $procName = $pp.ProcessName }
            } catch {}

            # Skip browser windows!
            if ($browserProcesses -contains $procName) { continue }

            # Match by process name (most reliable)
            if ($setupProcesses -contains $procName) {
                Log ""  UIA: '$wName' (PID=$wPid) process '$procName'""
                $setupWin = $w
                break
            }

            # Match by title (only non-browser)
            foreach ($t in $setupTitles) {
                if ($wName -like ""*$t*"") {
                    Log ""  UIA: '$wName' (PID=$wPid) title '$t' proc='$procName'""
                    $setupWin = $w
                    break
                }
            }
            if ($setupWin) { break }
        } catch {}
    }

    if (-not $setupWin) { return $false }

    # Handle radio buttons
    try {
        $radioCondition = New-Object ($global:tPC.FullName) @(
            $global:tAE::ControlTypeProperty,
            $global:tCT::RadioButton
        )
        $radios = $setupWin.FindAll($global:tTS::Descendants, $radioCondition)
        if ($radios.Count -gt 0) {
            Log ""  UIA: $($radios.Count) radio buttons""
            foreach ($r in $radios) {
                $rName = $r.Current.Name
                foreach ($kt in $keepFileTexts) {
                    if ($rName -like ""*$kt*"") {
                        try {
                            $sp = $r.GetCurrentPattern($global:tSIP::Pattern)
                            if (-not $sp.Current.IsSelected) {
                                $sp.Select()
                                Log ""    >>> Selected: '$rName' <<<""
                            }
                        } catch {}
                        break
                    }
                }
            }
        }
    } catch {}

    # Find and click buttons
    $btnCondition = New-Object ($global:tPC.FullName) @(
        $global:tAE::ControlTypeProperty,
        $global:tCT::Button
    )
    $buttons = $setupWin.FindAll($global:tTS::Descendants, $btnCondition)
    Log ""  UIA: $($buttons.Count) buttons""

    foreach ($btn in $buttons) {
        try {
            $bName = $btn.Current.Name
            $bEnabled = $btn.Current.IsEnabled

            $skip = $false
            foreach ($ex in $excludeButtons) {
                if ($bName -eq $ex) { $skip = $true; break }
            }
            if ($skip) { continue }

            foreach ($tgt in $targetButtons) {
                if ($bName -eq $tgt -or $bName -like ""*$tgt*"") {
                    Log ""    '$bName' MATCH '$tgt' enabled=$bEnabled""

                    if (-not $bEnabled) { break }

                    # InvokePattern
                    try {
                        $ip = $btn.GetCurrentPattern($global:tIP::Pattern)
                        $ip.Invoke()
                        Log ""    >>> CLICKED '$bName' via Invoke <<<""
                        return $true
                    } catch {
                        Log ""    Invoke failed: $_""
                    }

                    # SetFocus + SendKeys
                    try {
                        $btn.SetFocus()
                        Start-Sleep -Milliseconds 200
                        if ($sendKeysLoaded) {
                            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                            Log ""    >>> CLICKED '$bName' via Focus+Enter <<<""
                            return $true
                        }
                    } catch {
                        Log ""    Focus+Enter failed: $_""
                    }
                    break
                }
            }
        } catch {}
    }
    return $false
}

# ============================================================
# Fallback: WScript.Shell AppActivate + SendKeys (NO type literals!)
# ============================================================
$fallbackClickBlock = {
    $clicked = $false

    # Find setup process (exclude browsers!)
    $setupProc = $null
    foreach ($pName in $setupProcesses) {
        $procs = Get-Process -Name $pName -ErrorAction SilentlyContinue
        foreach ($p in $procs) {
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
                $setupProc = $p
                break
            }
        }
        if ($setupProc) { break }
    }

    # Also try by window title (but exclude browsers)
    if (-not $setupProc) {
        $allProcs = Get-Process | Where-Object {
            $_.MainWindowHandle -ne [IntPtr]::Zero -and
            $_.MainWindowTitle -ne '' -and
            ($browserProcesses -notcontains $_.ProcessName)
        }
        foreach ($p in $allProcs) {
            foreach ($t in $setupTitles) {
                if ($p.MainWindowTitle -like ""*$t*"") {
                    $setupProc = $p
                    break
                }
            }
            if ($setupProc) { break }
        }
    }

    if (-not $setupProc -or -not $wsh -or -not $sendKeysLoaded) {
        return $false
    }

    $procId = $setupProc.Id

    # Track same window — escalate Tab count
    if ($procId -eq $global:lastSetupPid) {
        $global:samePidStreak++
    } else {
        $global:samePidStreak = 0
        $global:lastSetupPid = $procId
    }

    # Activate the window
    try {
        $activated = $wsh.AppActivate($procId)
        if (-not $activated) { return $false }
    } catch { return $false }

    Start-Sleep -Milliseconds 500

    # Choose key strategy based on how many times we've seen this window
    # Cycle: ENTER → Tab+ENTER → Tab×3+ENTER → Tab×4+ENTER → Tab×5+ENTER → repeat
    $strategy = $global:samePidStreak % 6
    switch ($strategy) {
        0 {
            # Just ENTER (works for pages where button is focused)
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Log ""    >>> ENTER (PID=$procId, streak=$($global:samePidStreak)) <<<""
        }
        1 {
            # Tab + ENTER
            [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
            Start-Sleep -Milliseconds 150
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Log ""    >>> Tab+ENTER (PID=$procId) <<<""
        }
        2 {
            # 3 Tabs + ENTER (skip links, get to first button)
            for ($i = 0; $i -lt 3; $i++) {
                [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
                Start-Sleep -Milliseconds 100
            }
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Log ""    >>> Tab*3+ENTER (PID=$procId) <<<""
        }
        3 {
            # 4 Tabs + ENTER (get to Accept/Next button)
            for ($i = 0; $i -lt 4; $i++) {
                [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
                Start-Sleep -Milliseconds 100
            }
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Log ""    >>> Tab*4+ENTER (PID=$procId) <<<""
        }
        4 {
            # 5 Tabs + ENTER
            for ($i = 0; $i -lt 5; $i++) {
                [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
                Start-Sleep -Milliseconds 100
            }
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Log ""    >>> Tab*5+ENTER (PID=$procId) <<<""
        }
        5 {
            # Alt+A — accelerator for Accept/Прийняти
            [System.Windows.Forms.SendKeys]::SendWait('%a')
            Start-Sleep -Milliseconds 200
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Log ""    >>> Alt+A+ENTER (PID=$procId) <<<""
        }
    }
    $clicked = $true

    return $clicked
}

# ============================================================
# Main loop
# ============================================================
$timeout = (Get-Date).AddMinutes(15)
$clickCount = 0
$maxClicks = 30
$waitCycles = 0
$lastClickTime = $null

Log 'Main loop started...'
Log ""UIA=$uiaLoaded, SendKeys=$sendKeysLoaded, WScript=$($wsh -ne $null)""

while ((Get-Date) -lt $timeout -and $clickCount -lt $maxClicks) {
    Start-Sleep -Seconds 3
    $waitCycles++

    # Log every 10 cycles
    if ($waitCycles % 10 -eq 0) {
        Log ""Status: cycles=$waitCycles, clicks=$clickCount""
        $allWin = Get-Process | Where-Object {
            $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -ne ''
        } | Select-Object ProcessName, MainWindowTitle -First 15
        foreach ($w in $allWin) {
            Log ""  [$($w.ProcessName)] '$($w.MainWindowTitle)'""
        }
    }

    $didClick = $false

    # === PRIMARY: UIA (via ScriptBlock — safe compilation) ===
    if ($uiaLoaded) {
        try {
            $didClick = & $uiaClickBlock
        } catch {
            Log ""UIA block error: $_""
        }
    }

    # === FALLBACK: AppActivate + SendKeys ===
    if (-not $didClick) {
        try {
            $didClick = & $fallbackClickBlock
        } catch {
            Log ""Fallback block error: $_""
        }
    }

    if ($didClick) {
        $clickCount++
        $lastClickTime = Get-Date
        Log "">>> Click #$clickCount <<<""
        Start-Sleep -Seconds 5
    }

    # Smart stop
    if ($lastClickTime -and $clickCount -ge 3) {
        $sinceLastClick = ((Get-Date) - $lastClickTime).TotalSeconds
        if ($sinceLastClick -gt 120) {
            Log ""No buttons for 2 min after $clickCount clicks — installing""
            break
        }
    }
}

if ($clickCount -eq 0) {
    Log 'WARNING: No setup window found!'
    $allWin = Get-Process | Where-Object {
        $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -ne ''
    } | Select-Object ProcessName, MainWindowTitle
    foreach ($w in $allWin) {
        Log ""  [$($w.ProcessName)] '$($w.MainWindowTitle)'""
    }
}

Log ""=== AutoClicker v4.2 finished ($clickCount clicks) ===""
# Copy log to Desktop for easy access
try { Copy-Item -Path $logFile -Destination $deskLog -Force -ErrorAction SilentlyContinue } catch {}
exit 0
";

        // Write with UTF-8 BOM — PowerShell 5.1 needs BOM to read UTF-8 correctly!
        await File.WriteAllTextAsync(scriptPath, script, new System.Text.UTF8Encoding(true));
        DLog($"AutoClicker v4.1 script saved: {scriptPath}");
        return scriptPath;
    }

    /// <summary>
    /// Перевіряємо що є в ISO: які редакції, install.wim чи install.esd,
    /// чи збігається з поточною Windows. Логуємо все для діагностики.
    /// </summary>
    private static async Task ValidateIsoContentsAsync(string mountedDrive)
    {
        DLog("=== ISO VALIDATION ===");

        // 1. Перевіряємо яка Windows стоїть зараз
        var currentEdition = await GetCurrentWindowsEditionAsync();
        DLog($"Current Windows: {currentEdition}");

        // 2. Шукаємо install.wim або install.esd
        var sourcesDir = Path.Combine(mountedDrive, "sources");
        var installWim = Path.Combine(sourcesDir, "install.wim");
        var installEsd = Path.Combine(sourcesDir, "install.esd");

        string? wimFile = null;
        if (File.Exists(installWim))
        {
            wimFile = installWim;
            DLog($"Found: install.wim ({new FileInfo(installWim).Length / 1024 / 1024} MB)");
        }
        else if (File.Exists(installEsd))
        {
            wimFile = installEsd;
            DLog($"Found: install.esd ({new FileInfo(installEsd).Length / 1024 / 1024} MB)");
        }
        else
        {
            DLog("⚠️ Neither install.wim nor install.esd found in sources!");
            // Перелічуємо що є в sources для діагностики
            try
            {
                var files = Directory.GetFiles(sourcesDir).Select(Path.GetFileName).Take(30);
                DLog($"Files in sources: {string.Join(", ", files)}");
            }
            catch (Exception ex) { DLog($"Cannot list sources: {ex.Message}"); }
            return;
        }

        // 3. Перевіряємо ei.cfg (визначає тип ліцензії: Retail, Volume, OEM)
        var eiCfg = Path.Combine(sourcesDir, "ei.cfg");
        if (File.Exists(eiCfg))
        {
            try
            {
                var content = await File.ReadAllTextAsync(eiCfg);
                DLog($"ei.cfg content: {content.Trim()}");
            }
            catch { }
        }
        else
        {
            DLog("ei.cfg: not found (multi-edition ISO)");
        }

        // 4. Отримуємо список редакцій через DISM
        try
        {
            var dismScript = $@"
                $wimFile = '{wimFile!.Replace("'", "''")}'
                $result = dism /Get-WimInfo /WimFile:$wimFile 2>&1
                Write-Output $result
            ";
            var dismOutput = await RunPowerShellAsync(dismScript);
            DLog($"DISM /Get-WimInfo output:");
            foreach (var line in dismOutput.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                DLog($"  {line.Trim()}");
            }

            // Перевіряємо чи є поточна редакція в ISO
            var outputLower = dismOutput.ToLowerInvariant();
            var editionLower = currentEdition.ToLowerInvariant();

            // Шукаємо ключові слова з поточної редакції
            var matchFound = false;
            if (editionLower.Contains("pro") && outputLower.Contains("pro"))
                matchFound = true;
            else if (editionLower.Contains("home") && outputLower.Contains("home"))
                matchFound = true;
            else if (editionLower.Contains("enterprise") && outputLower.Contains("enterprise"))
                matchFound = true;
            else if (editionLower.Contains("education") && outputLower.Contains("education"))
                matchFound = true;

            if (matchFound)
                DLog($"✅ Edition match found: current={currentEdition}");
            else
                DLog($"⚠️ Edition MISMATCH! Current: {currentEdition}, ISO might not contain this edition!");
        }
        catch (Exception ex)
        {
            DLog($"DISM validation failed: {ex.Message}");
        }

        // 5. Перевіряємо PID.txt
        var pidTxt = Path.Combine(sourcesDir, "PID.txt");
        if (File.Exists(pidTxt))
        {
            try
            {
                var content = await File.ReadAllTextAsync(pidTxt);
                DLog($"PID.txt: {content.Trim()}");
            }
            catch { }
        }

        // 6. Перевіряємо мову ISO vs мову системи
        // ВАЖЛИВО: setup.exe перевіряє InstallLanguage з реєстру, а НЕ Get-UICulture!
        // Get-UICulture повертає стару мову до ребуту, тому читаємо реєстр напряму.
        try
        {
            var langCheckScript = @"
                $uiCulture = (Get-UICulture).Name
                $systemLocale = (Get-WinSystemLocale).Name
                $nlsPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\Language'
                $installLang = (Get-ItemProperty $nlsPath).InstallLanguage
                Write-Output ""SystemUI=$uiCulture""
                Write-Output ""SystemLocale=$systemLocale""
                Write-Output ""InstallLanguage=$installLang""
            ";
            var langResult = await RunPowerShellAsync(langCheckScript);
            DLog($"System language info: {langResult.Trim().Replace("\n", ", ")}");

            // Перевіряємо мову install.wim (перший індекс)
            var wimLangScript = $@"
                $output = dism /Get-WimInfo /WimFile:'{wimFile!.Replace("'", "''")}' /Index:1 2>&1
                $langLines = $output | Where-Object {{ $_ -match 'Language|Default Language|Мова' }}
                foreach ($l in $langLines) {{ Write-Output $l.ToString().Trim() }}
            ";
            var wimLangResult = await RunPowerShellAsync(wimLangScript);
            DLog($"ISO language: {wimLangResult.Trim()}");

            // Порівнюємо InstallLanguage з реєстру з мовою ISO
            // InstallLanguage hex codes: 0409=en-US, 0419=ru-RU, 0422=uk-UA
            var isoLang = wimLangResult.ToLowerInvariant();
            var installLangHex = "";
            foreach (var line in langResult.Split('\n'))
            {
                if (line.Trim().StartsWith("InstallLanguage="))
                    installLangHex = line.Trim().Split('=').Last().Trim().ToLowerInvariant();
            }

            // Маппінг hex → мова для порівняння з ISO
            var installLangName = installLangHex switch
            {
                "0409" => "en-us",
                "0419" => "ru-ru",
                "0422" => "uk-ua",
                "0809" => "en-gb",
                _ => installLangHex
            };

            DLog($"InstallLanguage registry: {installLangHex} → {installLangName}");

            if (!string.IsNullOrEmpty(installLangName) && isoLang.Contains(installLangName))
            {
                DLog("✅ ISO language matches InstallLanguage registry — 'Keep files & apps' should work!");
            }
            else if (!string.IsNullOrEmpty(installLangName) && installLangName.StartsWith("en") && (isoLang.Contains("en-us") || isoLang.Contains("en-gb")))
            {
                DLog("✅ ISO language matches InstallLanguage registry (English) — 'Keep files & apps' should work!");
            }
            else
            {
                DLog($"⚠️⚠️⚠️ LANGUAGE MISMATCH! InstallLanguage={installLangHex} ({installLangName}) vs ISO language. 'Keep files & apps' may be BLOCKED!");
            }
        }
        catch (Exception ex)
        {
            DLog($"Language validation failed: {ex.Message}");
        }

        DLog("=== ISO VALIDATION COMPLETE ===");
    }

    /// <summary>
    /// Отримати поточну редакцію Windows (Pro, Home, Enterprise тощо).
    /// </summary>
    private static async Task<string> GetCurrentWindowsEditionAsync()
    {
        try
        {
            // Отримуємо і версію і редакцію
            var script = @"
                $os = Get-WmiObject Win32_OperatingSystem
                $edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
                $productName = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName
                $build = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild
                $displayVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion
                Write-Output ""Edition: $edition""
                Write-Output ""ProductName: $productName""
                Write-Output ""Build: $build""
                Write-Output ""DisplayVersion: $displayVersion""
                Write-Output ""Caption: $($os.Caption)""
            ";
            var result = await RunPowerShellAsync(script);
            DLog($"Current Windows info:");
            foreach (var line in result.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                DLog($"  {line.Trim()}");
            }

            // Повертаємо EditionID (Pro, Home, Enterprise тощо)
            var editionLine = result.Split('\n')
                .FirstOrDefault(l => l.Trim().StartsWith("Edition:"));
            if (editionLine != null)
            {
                return editionLine.Split(':').Last().Trim();
            }

            return result.Split('\n').FirstOrDefault()?.Trim() ?? "Unknown";
        }
        catch (Exception ex)
        {
            DLog($"Cannot get Windows edition: {ex.Message}");
            return "Unknown";
        }
    }

    /// <summary>
    /// Run PowerShell script and return stdout.
    /// </summary>
    private static async Task<string> RunPowerShellAsync(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return "";

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!string.IsNullOrEmpty(error))
            DLog($"PS Error: {error.Trim()}");

        return output;
    }
}
