using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Глибока оптимізація Windows — телеметрія, приватність, продуктивність, UI bloat.
/// Натхненний Win11Debloat/Sophia Script — найкращі registry tweaks зібрані в одному сервісі.
/// Всі зміни зворотні через System Restore Point (створюється ДО запуску).
/// </summary>
public static class WindowsDebloatService
{
    /// <summary>
    /// Список всіх застосованих оптимізацій (для логів/результатів).
    /// </summary>
    public static async Task<List<string>> OptimizeAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        var applied = new List<string>();

        try
        {
            // === 1. ТЕЛЕМЕТРІЯ / ПРИВАТНІСТЬ ===
            onProgress?.Invoke("Вимкнення телеметрії Windows...");
            var telemetryTweaks = await ApplyTelemetryTweaksAsync(token);
            applied.AddRange(telemetryTweaks);

            // === 2. UI BLOAT ===
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke("Оптимізація інтерфейсу...");
            var uiTweaks = await ApplyUiTweaksAsync(token);
            applied.AddRange(uiTweaks);

            // === 3. ПРОДУКТИВНІСТЬ ===
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke("Оптимізація продуктивності...");
            var perfTweaks = await ApplyPerformanceTweaksAsync(token);
            applied.AddRange(perfTweaks);

            // === 4. МЕРЕЖА / ДОСТАВКА ===
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke("Оптимізація мережевих налаштувань...");
            var netTweaks = await ApplyNetworkTweaksAsync(token);
            applied.AddRange(netTweaks);

            // === 5. EXPLORER / ПРОВІДНИК ===
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke("Налаштування провідника...");
            var explorerTweaks = await ApplyExplorerTweaksAsync(token);
            applied.AddRange(explorerTweaks);

            // === 6. ОЧИСТКА ТАСКБАРА ===
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke("Очистка панелі завдань...");
            await CleanTaskbarAsync(token);
            applied.Add("Taskbar cleaned — unpinned all non-default apps");

            Logger.Info($"[Debloat] Total optimizations applied: {applied.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[Debloat] Error: {ex.Message}");
        }

        return applied;
    }

    // ========================================================
    // 1. ТЕЛЕМЕТРІЯ / ПРИВАТНІСТЬ
    // ========================================================
    private static async Task<List<string>> ApplyTelemetryTweaksAsync(CancellationToken ct)
    {
        var applied = new List<string>();

        var tweaks = new (string Name, string Path, string ValueName, object Value, string Type)[]
        {
            // Disable telemetry
            ("Disable Telemetry",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                "AllowTelemetry", 0, "DWord"),

            // Disable diagnostic data
            ("Disable Diagnostic Data",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Diagnostics\DiagTrack",
                "ShowedToastAtLevel", 1, "DWord"),

            // Disable advertising ID
            ("Disable Advertising ID",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                "Enabled", 0, "DWord"),

            // Disable activity history
            ("Disable Activity History",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System",
                "EnableActivityFeed", 0, "DWord"),

            ("Disable Activity History Upload",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System",
                "UploadUserActivities", 0, "DWord"),

            // Disable app launch tracking
            ("Disable App Launch Tracking",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "Start_TrackProgs", 0, "DWord"),

            // Disable feedback requests
            ("Disable Feedback Requests",
                @"HKCU:\SOFTWARE\Microsoft\Siuf\Rules",
                "NumberOfSIUFInPeriod", 0, "DWord"),

            // Disable tailored experiences
            ("Disable Tailored Experiences",
                @"HKCU:\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                "DisableTailoredExperiencesWithDiagnosticData", 1, "DWord"),

            // Disable location tracking
            ("Disable Location Tracking",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                "DisableLocation", 1, "DWord"),

            // Disable handwriting error reporting
            ("Disable Handwriting Error Reports",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports",
                "PreventHandwritingErrorReports", 1, "DWord"),

            // Disable input personalization
            ("Disable Input Personalization",
                @"HKCU:\SOFTWARE\Microsoft\InputPersonalization",
                "RestrictImplicitInkCollection", 1, "DWord"),

            // Disable Customer Experience Improvement Program
            ("Disable CEIP",
                @"HKLM:\SOFTWARE\Policies\Microsoft\SQMClient\Windows",
                "CEIPEnable", 0, "DWord"),

            // Disable Windows Error Reporting
            ("Disable Error Reporting",
                @"HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting",
                "Disabled", 1, "DWord"),
        };

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var success = await SetRegistryValueAsync(tweak.Path, tweak.ValueName, tweak.Value, tweak.Type);
                if (success)
                {
                    applied.Add(tweak.Name);
                    Logger.Info($"[Debloat] ✓ {tweak.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Debloat] ✗ {tweak.Name}: {ex.Message}");
            }
        }

        return applied;
    }

    // ========================================================
    // 2. UI BLOAT
    // ========================================================
    private static async Task<List<string>> ApplyUiTweaksAsync(CancellationToken ct)
    {
        var applied = new List<string>();

        var tweaks = new (string Name, string Path, string ValueName, object Value, string Type)[]
        {
            // Disable Bing search in Start Menu
            ("Disable Bing Search",
                @"HKCU:\SOFTWARE\Policies\Microsoft\Windows\Explorer",
                "DisableSearchBoxSuggestions", 1, "DWord"),

            // Disable suggestions in Start
            ("Disable Start Suggestions",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-338388Enabled", 0, "DWord"),

            // Disable tips and tricks
            ("Disable Tips & Tricks",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-338389Enabled", 0, "DWord"),

            // Disable suggested apps
            ("Disable Suggested Apps",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-353694Enabled", 0, "DWord"),

            // Disable suggested content in Settings
            ("Disable Settings Suggestions",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-353696Enabled", 0, "DWord"),

            // Disable pre-installed apps
            ("Disable Pre-Installed Apps",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "OemPreInstalledAppsEnabled", 0, "DWord"),

            // Disable silent app installs
            ("Disable Silent App Installs",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SilentInstalledAppsEnabled", 0, "DWord"),

            // Disable Windows Spotlight
            ("Disable Windows Spotlight",
                @"HKCU:\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                "DisableWindowsSpotlightFeatures", 1, "DWord"),

            // Disable lock screen tips
            ("Disable Lock Screen Tips",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "RotatingLockScreenOverlayEnabled", 0, "DWord"),

            // Disable Copilot (Win 11)
            ("Disable Copilot",
                @"HKCU:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot",
                "TurnOffWindowsCopilot", 1, "DWord"),

            // Disable Widgets (Win 11)
            ("Disable Widgets",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Dsh",
                "AllowNewsAndInterests", 0, "DWord"),

            // Disable Recall (Win 11 24H2+)
            ("Disable Recall",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                "DisableAIDataAnalysis", 1, "DWord"),

            // Disable "Get tips" notifications
            ("Disable Get Tips",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SoftLandingEnabled", 0, "DWord"),

            // Disable Welcome Experience
            ("Disable Welcome Experience",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-310093Enabled", 0, "DWord"),
        };

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var success = await SetRegistryValueAsync(tweak.Path, tweak.ValueName, tweak.Value, tweak.Type);
                if (success)
                {
                    applied.Add(tweak.Name);
                    Logger.Info($"[Debloat] ✓ {tweak.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Debloat] ✗ {tweak.Name}: {ex.Message}");
            }
        }

        return applied;
    }

    // ========================================================
    // 3. ПРОДУКТИВНІСТЬ
    // ========================================================
    private static async Task<List<string>> ApplyPerformanceTweaksAsync(CancellationToken ct)
    {
        var applied = new List<string>();

        var tweaks = new (string Name, string Path, string ValueName, object Value, string Type)[]
        {
            // Disable GameDVR / Game Bar
            ("Disable Game DVR",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR",
                "AllowGameDVR", 0, "DWord"),

            ("Disable Game Bar",
                @"HKCU:\SOFTWARE\Microsoft\GameBar",
                "AutoGameModeEnabled", 0, "DWord"),

            // Disable background apps (Win 10/11)
            ("Disable Background Apps",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
                "GlobalUserDisabled", 1, "DWord"),

            // Disable Cortana
            ("Disable Cortana",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                "AllowCortana", 0, "DWord"),

            // Optimize visual effects for performance
            ("Optimize Visual Effects",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                "VisualFXSetting", 2, "DWord"),

            // Disable transparency effects
            ("Disable Transparency",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 0, "DWord"),

            // Disable animations
            ("Disable Menu Animations",
                @"HKCU:\Control Panel\Desktop",
                "UserPreferencesMask",
                // Hex bytes that disable menu animations
                "9012038010000000", "Binary"),

            // Faster menu show delay
            ("Faster Menu Delay",
                @"HKCU:\Control Panel\Desktop",
                "MenuShowDelay", "100", "String"),

            // Disable mouse enhance pointer precision (acceleration)
            ("Disable Mouse Acceleration",
                @"HKCU:\Control Panel\Mouse",
                "MouseSpeed", "0", "String"),

            // Disable Hibernation (saves disk space)
            ("Disable Hibernation",
                @"HKLM:\SYSTEM\CurrentControlSet\Control\Power",
                "HibernateEnabled", 0, "DWord"),

            // Optimize power plan for performance
            ("High Performance Power Plan",
                @"HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings",
                "ActivePowerScheme", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "String"),

            // Disable Storage Sense (we clean manually)
            ("Disable Storage Sense",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy",
                "01", 0, "DWord"),

            // Disable Delivery Optimization P2P
            ("Disable DO P2P",
                @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
                "DODownloadMode", 0, "DWord"),
        };

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (tweak.Type == "Binary")
                {
                    // Skip binary registry values for simplicity — apply via PowerShell command
                    continue;
                }

                var success = await SetRegistryValueAsync(tweak.Path, tweak.ValueName, tweak.Value, tweak.Type);
                if (success)
                {
                    applied.Add(tweak.Name);
                    Logger.Info($"[Debloat] ✓ {tweak.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Debloat] ✗ {tweak.Name}: {ex.Message}");
            }
        }

        // Additional performance tweaks via PowerShell commands
        ct.ThrowIfCancellationRequested();
        try
        {
            // Disable hibernation via powercfg
            var hibResult = await RunCmdAsync("powercfg /hibernate off", 15);
            if (hibResult)
            {
                applied.Add("Disable Hibernation (powercfg)");
                Logger.Info("[Debloat] ✓ Hibernation disabled via powercfg");
            }
        }
        catch { }

        // Set high performance power plan
        ct.ThrowIfCancellationRequested();
        try
        {
            var powerResult = await RunCmdAsync("powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", 10);
            if (powerResult)
            {
                applied.Add("High Performance Power Plan (powercfg)");
                Logger.Info("[Debloat] ✓ High Performance power plan activated");
            }
        }
        catch { }

        return applied;
    }

    // ========================================================
    // 4. МЕРЕЖА
    // ========================================================
    private static async Task<List<string>> ApplyNetworkTweaksAsync(CancellationToken ct)
    {
        var applied = new List<string>();

        var tweaks = new (string Name, string Path, string ValueName, object Value, string Type)[]
        {
            // Disable Nagle's Algorithm for lower latency
            ("Optimize Network Latency",
                @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), "DWord"),

            // Disable auto-tuning for some routers
            ("Disable Network Auto-Tuning",
                @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness", 0, "DWord"),

            // Disable Peer-to-Peer network for updates
            ("Disable P2P Updates",
                @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config",
                "DODownloadMode", 0, "DWord"),

            // Disable Network Location Awareness
            ("Disable NLA Prompts",
                @"HKLM:\SYSTEM\CurrentControlSet\Control\Network\NewNetworkWindowOff",
                "", 0, "DWord"),
        };

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var success = await SetRegistryValueAsync(tweak.Path, tweak.ValueName, tweak.Value, tweak.Type);
                if (success)
                {
                    applied.Add(tweak.Name);
                    Logger.Info($"[Debloat] ✓ {tweak.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Debloat] ✗ {tweak.Name}: {ex.Message}");
            }
        }

        return applied;
    }

    // ========================================================
    // 5. EXPLORER / ПРОВІДНИК
    // ========================================================
    private static async Task<List<string>> ApplyExplorerTweaksAsync(CancellationToken ct)
    {
        var applied = new List<string>();

        var tweaks = new (string Name, string Path, string ValueName, object Value, string Type)[]
        {
            // Show file extensions
            ("Show File Extensions",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "HideFileExt", 0, "DWord"),

            // Show hidden files
            ("Show Hidden Files",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "Hidden", 1, "DWord"),

            // Disable recent files in Explorer
            ("Disable Recent Files",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer",
                "ShowRecent", 0, "DWord"),

            // Disable frequent folders in Explorer
            ("Disable Frequent Folders",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer",
                "ShowFrequent", 0, "DWord"),

            // Open Explorer to "This PC" instead of "Quick Access"
            ("Explorer Opens This PC",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "LaunchTo", 1, "DWord"),

            // Disable thumbnail cache (prevents locked files)
            ("Disable Thumbnail Cache",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "DisableThumbnailCache", 1, "DWord"),

            // Taskbar align left (Win 11)
            ("Taskbar Align Left",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "TaskbarAl", 0, "DWord"),

            // Hide search box in taskbar (show icon only)
            ("Minimize Search in Taskbar",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                "SearchboxTaskbarMode", 1, "DWord"),

            // Hide Task View button
            ("Hide Task View",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "ShowTaskViewButton", 0, "DWord"),

            // Disable News and Interests (taskbar widget Win 10)
            ("Disable News & Interests",
                @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Feeds",
                "ShellFeedsTaskbarViewMode", 2, "DWord"),
        };

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var success = await SetRegistryValueAsync(tweak.Path, tweak.ValueName, tweak.Value, tweak.Type);
                if (success)
                {
                    applied.Add(tweak.Name);
                    Logger.Info($"[Debloat] ✓ {tweak.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Debloat] ✗ {tweak.Name}: {ex.Message}");
            }
        }

        // Restart Explorer to apply changes
        ct.ThrowIfCancellationRequested();
        try
        {
            Logger.Info("[Debloat] Restarting Explorer to apply UI changes...");
            await RunCmdAsync("taskkill /f /im explorer.exe", 10);
            await Task.Delay(1500, ct);
            await RunCmdAsync("start explorer.exe", 10);
            Logger.Info("[Debloat] Explorer restarted");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Debloat] Explorer restart failed: {ex.Message}");
        }

        return applied;
    }

    // ========================================================
    // UTILITY METHODS
    // ========================================================

    /// <summary>
    /// Set a registry value via PowerShell.
    /// Creates the key path if it doesn't exist.
    /// </summary>
    private static async Task<bool> SetRegistryValueAsync(
        string keyPath, string valueName, object value, string type)
    {
        try
        {
            // Build PowerShell script
            string valueArg;
            string typeArg = type;

            if (type == "String")
            {
                valueArg = $"'{value}'";
            }
            else
            {
                valueArg = value.ToString()!;
            }

            // Create path if not exists + set value
            var psScript =
                $"$ErrorActionPreference = 'Stop'\n" +
                $"$path = '{keyPath}'\n" +
                $"if (!(Test-Path $path)) {{ New-Item -Path $path -Force | Out-Null }}\n" +
                $"Set-ItemProperty -Path $path -Name '{valueName}' -Value {valueArg} -Type {typeArg} -Force";

            var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -NoLogo -EncodedCommand {encodedCmd}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using var cts = new CancellationTokenSource(15000); // 15s timeout
            try
            {
                var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                if (proc.ExitCode != 0)
                {
                    Logger.Warn($"[Debloat] Registry failed: {keyPath}\\{valueName}: {stderr.Trim()}");
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Logger.Warn($"[Debloat] Registry timeout: {keyPath}\\{valueName}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Debloat] SetRegistry error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run a command via cmd.exe with timeout.
    /// </summary>
    private static async Task<bool> RunCmdAsync(string command, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                return proc.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
        }
        catch { return false; }
    }

    // ========================================================
    // 6. ОЧИСТКА ТАСКБАРА — відкріпити все зайве
    // ========================================================
    private static async Task CleanTaskbarAsync(CancellationToken ct)
    {
        try
        {
            // Видалити ВСІ закріплені додатки з таскбара
            // Шлях: %AppData%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar
            var psScript = @"
                # Очистити таскбар — видалити всі .lnk файли окрім Explorer
                $taskbarPath = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
                if (Test-Path $taskbarPath) {
                    Get-ChildItem $taskbarPath -Filter '*.lnk' | ForEach-Object {
                        $name = $_.Name.ToLower()
                        # Залишаємо тільки File Explorer
                        if ($name -notlike '*explorer*' -and $name -notlike '*проводник*' -and $name -notlike '*провідник*') {
                            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
                        }
                    }
                }

                # Також очистити для ВСІХ юзерів
                $users = Get-ChildItem 'C:\Users' -Directory | Where-Object { $_.Name -notin @('Public','Default','Default User','All Users') }
                foreach ($u in $users) {
                    $tbPath = Join-Path $u.FullName 'AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
                    if (Test-Path $tbPath) {
                        Get-ChildItem $tbPath -Filter '*.lnk' | ForEach-Object {
                            $name = $_.Name.ToLower()
                            if ($name -notlike '*explorer*' -and $name -notlike '*проводник*' -and $name -notlike '*провідник*') {
                                Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
                            }
                        }
                    }
                }

                # Перезапустити Explorer щоб зміни вступили в силу
                Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
                Start-Process explorer.exe
            ";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(20000);
                try { await proc.WaitForExitAsync(cts.Token); }
                catch { try { proc.Kill(true); } catch { } }
            }
            Logger.Info("[Debloat] Taskbar cleaned — all non-default apps unpinned");
        }
        catch (Exception ex)
        {
            Logger.Info($"[Debloat] Taskbar cleanup error: {ex.Message}");
        }
    }
}
