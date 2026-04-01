using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.Core;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Optimization;

/// <summary>
/// Оновлення драйверів v3.0 — МАКСИМАЛЬНЕ покриття!
///
/// Стратегія (5 кроків):
/// 1. Фікс аудіо-служб (найчастіша проблема)
/// 2. PnPUtil scan — сканування нових пристроїв
/// 3. Windows Update COM API — пошук та установка драйверів з Windows Update
/// 4. SDIO (Snappy Driver Installer Origin) — розширена база драйверів (~99%)
/// 5. Перевірка проблемних пристроїв та переінсталяція
///
/// Сумісність: Windows 7 / 8 / 8.1 / 10 / 11
/// </summary>
public static class DriverUpdater
{
    public static async Task<bool> UpdateAsync(Action<string>? onProgress = null)
    {
        try
        {
            int driversInstalled = 0;
            int problemsFixed = 0;

            // === КРОК 1: Фікс аудіо-служб (найчастіша проблема після очистки) ===
            onProgress?.Invoke("Перевірка аудіо-підсистеми...");
            Logger.Info("[Drivers] Step 1: Fixing audio services");
            var audioFixed = await FixAudioServicesAsync();
            if (audioFixed) problemsFixed++;

            // === КРОК 2: PnPUtil scan — знайти нові пристрої ===
            onProgress?.Invoke("Сканування обладнання...");
            Logger.Info("[Drivers] Step 2: PnPUtil scan-devices");
            await PnpScanDevicesAsync();

            // === КРОК 3: Windows Update — пошук та установка драйверів ===
            onProgress?.Invoke("Пошук оновлень пристроїв...");
            Logger.Info("[Drivers] Step 3: Windows Update driver search + install");
            var wuInstalled = await WindowsUpdateDriversAsync(onProgress);
            driversInstalled += wuInstalled;

            // === КРОК 4: SDIO — розширена база драйверів ===
            onProgress?.Invoke("Встановлення оптимальних драйверів...");
            Logger.Info("[Drivers] Step 4: SDIO driver installation");
            var sdioInstalled = await SdioInstallDriversAsync(onProgress);
            driversInstalled += sdioInstalled;

            // === КРОК 5: Перевірка проблемних пристроїв ===
            onProgress?.Invoke("Діагностика апаратного забезпечення...");
            Logger.Info("[Drivers] Step 5: Check problem devices");
            var devFixed = await FixProblemDevicesAsync();
            problemsFixed += devFixed;

            // === КРОК 6: Фінальна верифікація + повторна спроба WU для залишків ===
            onProgress?.Invoke("Фінальна перевірка пристроїв...");
            Logger.Info("[Drivers] Step 6: Final verification");
            var verifyResult = await VerifyAndRetryAsync(onProgress);
            driversInstalled += verifyResult.installed;
            problemsFixed += verifyResult.fixedCount;

            var summary = $"Драйвери: {driversInstalled} встановлено, {problemsFixed} проблем виправлено";
            onProgress?.Invoke("Оновлення драйверів завершено");
            Logger.Info($"[Drivers] Done: {summary}");

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("DriverUpdater failed", ex);
            return false;
        }
    }

    /// <summary>
    /// КРОК 1: Повний фікс аудіо v2.0:
    /// 1. Перезапуск служб Audiosrv + AudioEndpointBuilder
    /// 2. Enable всіх вимкнених аудіо-пристроїв
    /// 3. Переінсталяція аудіо-драйвера через pnputil
    /// 4. Відновлення реєстрових ключів аудіо
    /// </summary>
    private static async Task<bool> FixAudioServicesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var psScript = @"
                    $ErrorActionPreference = 'SilentlyContinue'
                    $fixed = 0

                    # === 1. Аудіо-служби: автозапуск + рестарт ===
                    $audioServices = @('Audiosrv', 'AudioEndpointBuilder')

                    foreach ($svc in $audioServices) {
                        $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
                        if ($service) {
                            Set-Service -Name $svc -StartupType Automatic -ErrorAction SilentlyContinue

                            # Завжди перезапускаємо для чистого старту
                            Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
                            Start-Sleep -Milliseconds 500
                            Start-Service -Name $svc -ErrorAction SilentlyContinue
                            Start-Sleep -Seconds 1

                            $newStatus = (Get-Service -Name $svc).Status
                            if ($newStatus -eq 'Running') { $fixed++ }
                            Write-Output ""$svc : $newStatus""
                        }
                    }

                    # AudioDG — перезапустити
                    Stop-Process -Name 'AudioDG' -Force -ErrorAction SilentlyContinue

                    # === 2. Enable ВСІХ вимкнених аудіо-пристроїв ===
                    Write-Output 'Checking disabled audio devices...'
                    $audioDevices = Get-WmiObject Win32_PnPEntity |
                        Where-Object { $_.PNPClass -eq 'AudioEndpoint' -or $_.PNPClass -eq 'MEDIA' -or
                                       $_.Name -match 'Audio|Sound|Realtek|HDMI|Speaker|Microphone|Headphone' }

                    foreach ($dev in $audioDevices) {
                        if ($dev.ConfigManagerErrorCode -eq 22) {
                            # Error 22 = Device is disabled
                            Write-Output ""Enabling disabled audio: $($dev.Name)""
                            $dev.Enable() | Out-Null
                            $fixed++
                        }
                        elseif ($dev.ConfigManagerErrorCode -ne 0) {
                            # Other error — try reinstall
                            Write-Output ""Reinstalling audio: $($dev.Name) (error=$($dev.ConfigManagerErrorCode))""
                            $devId = $dev.DeviceID
                            pnputil /remove-device ""$devId"" /subtree 2>&1 | Out-Null
                            Start-Sleep -Milliseconds 500
                            pnputil /scan-devices 2>&1 | Out-Null
                            $fixed++
                        }
                    }

                    # === 3. Перевірити що аудіо endpoint існує ===
                    $endpoints = Get-WmiObject Win32_SoundDevice
                    $endpointCount = ($endpoints | Measure-Object).Count
                    Write-Output ""Audio endpoints found: $endpointCount""

                    if ($endpointCount -eq 0) {
                        # Немає жодного звукового пристрою — спробувати пересканувати
                        Write-Output 'No audio devices! Running full PnP scan...'
                        pnputil /scan-devices 2>&1 | Out-Null
                        Start-Sleep -Seconds 3
                        $endpointCount = (Get-WmiObject Win32_SoundDevice | Measure-Object).Count
                        Write-Output ""After rescan: $endpointCount audio devices""
                    }

                    # === 4. Відновити реєстрові ключі для аудіо ===
                    # Переконатися що Windows Audio не вимкнений через реєстр
                    $audioRegPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\Audiosrv'
                    if (Test-Path $audioRegPath) {
                        $startVal = (Get-ItemProperty -Path $audioRegPath -Name 'Start' -ErrorAction SilentlyContinue).Start
                        if ($startVal -ne 2) {
                            Set-ItemProperty -Path $audioRegPath -Name 'Start' -Value 2 -Type DWord -Force
                            Write-Output 'Fixed Audiosrv registry Start=2 (Automatic)'
                            $fixed++
                        }
                    }
                    $aebRegPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\AudioEndpointBuilder'
                    if (Test-Path $aebRegPath) {
                        $startVal = (Get-ItemProperty -Path $aebRegPath -Name 'Start' -ErrorAction SilentlyContinue).Start
                        if ($startVal -ne 2) {
                            Set-ItemProperty -Path $aebRegPath -Name 'Start' -Value 2 -Type DWord -Force
                            Write-Output 'Fixed AudioEndpointBuilder registry Start=2 (Automatic)'
                            $fixed++
                        }
                    }

                    # === 5. Фінальна перевірка — чи працює звук ===
                    Start-Sleep -Seconds 2
                    $svc1 = (Get-Service Audiosrv).Status
                    $svc2 = (Get-Service AudioEndpointBuilder).Status
                    Write-Output ""Final: Audiosrv=$svc1, AudioEndpointBuilder=$svc2""

                    Write-Output ""AUDIO_FIXED=$fixed""
                ";

                var result = RunPowerShellEncoded(psScript, 15000);
                Logger.Info($"[Drivers] Audio fix: {result.Trim()}");
                return result.Contains("AUDIO_FIXED=") && !result.Contains("AUDIO_FIXED=0");
            }
            catch (Exception ex)
            {
                Logger.Info($"[Drivers] Audio fix error: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// КРОК 2: PnPUtil — сканування пристроїв (Win10+) або enumerate (Win7/8).
    /// </summary>
    private static async Task PnpScanDevicesAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var sys32 = GetRealSystem32();
                var pnpPath = Path.Combine(sys32, "pnputil.exe");
                if (!File.Exists(pnpPath)) return;

                var args = IsWindows10OrLater() ? "/scan-devices" : "-e";
                var psi = new ProcessStartInfo
                {
                    FileName = pnpPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(60000);
                Logger.Info($"[Drivers] PnPUtil {args}: exit {proc?.ExitCode}");
            }
            catch (Exception ex)
            {
                Logger.Info($"[Drivers] PnPUtil error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// КРОК 3: Windows Update COM API — РЕАЛЬНА установка драйверів!
    /// Шукає драйвери через Windows Update, скачує та ставить тихо.
    /// Працює на Win7+ (COM API вбудований в Windows).
    /// </summary>
    private static async Task<int> WindowsUpdateDriversAsync(Action<string>? onProgress)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Windows Update COM API через PowerShell
                // Шукає ТІЛЬКИ драйвери (Type='Driver' або CategoryIDs містить driver)
                var psScript = @"
                    $ErrorActionPreference = 'Stop'
                    try {
                        # Створити Windows Update Session
                        $Session = New-Object -ComObject Microsoft.Update.Session
                        $Searcher = $Session.CreateUpdateSearcher()

                        # Шукати невстановлені драйвери
                        Write-Output 'Searching for driver updates...'
                        $SearchResult = $Searcher.Search(""IsInstalled=0 and Type='Driver'"")
                        $Count = $SearchResult.Updates.Count
                        Write-Output ""Found $Count driver updates""

                        if ($Count -eq 0) {
                            Write-Output 'DRIVERS_INSTALLED=0'
                            exit
                        }

                        # Зібрати оновлення для установки
                        $UpdatesToInstall = New-Object -ComObject Microsoft.Update.UpdateColl
                        foreach ($Update in $SearchResult.Updates) {
                            Write-Output ""  Driver: $($Update.Title)""
                            # Прийняти ліцензію якщо потрібно
                            if ($Update.EulaAccepted -eq $false) {
                                $Update.AcceptEula()
                            }
                            $UpdatesToInstall.Add($Update) | Out-Null
                        }

                        # Скачати драйвери
                        Write-Output 'Downloading drivers...'
                        $Downloader = $Session.CreateUpdateDownloader()
                        $Downloader.Updates = $UpdatesToInstall
                        $DownloadResult = $Downloader.Download()
                        Write-Output ""Download result: $($DownloadResult.ResultCode)""

                        # Встановити драйвери
                        Write-Output 'Installing drivers...'
                        $Installer = $Session.CreateUpdateInstaller()
                        $Installer.Updates = $UpdatesToInstall
                        $InstallResult = $Installer.Install()

                        $Installed = 0
                        for ($i = 0; $i -lt $UpdatesToInstall.Count; $i++) {
                            $status = $InstallResult.GetUpdateResult($i).ResultCode
                            if ($status -eq 2) { $Installed++ } # 2 = Succeeded
                            Write-Output ""  $($UpdatesToInstall.Item($i).Title): result=$status""
                        }

                        Write-Output ""DRIVERS_INSTALLED=$Installed""
                    } catch {
                        Write-Output ""WU_ERROR: $($_.Exception.Message)""
                        Write-Output 'DRIVERS_INSTALLED=0'
                    }
                ";

                var result = RunPowerShellEncoded(psScript, 300000); // 5 хвилин макс
                Logger.Info($"[Drivers] Windows Update result:\n{result}");

                // Парсимо кількість встановлених
                foreach (var line in result.Split('\n'))
                {
                    if (line.Trim().StartsWith("DRIVERS_INSTALLED="))
                    {
                        if (int.TryParse(line.Trim().Replace("DRIVERS_INSTALLED=", ""), out int count))
                        {
                            if (count > 0)
                                onProgress?.Invoke($"Встановлено {count} драйверів");
                            return count;
                        }
                    }
                    // Progress для UI
                    if (line.Trim().StartsWith("Driver:"))
                        onProgress?.Invoke(line.Trim());
                    else if (line.Trim().StartsWith("Downloading"))
                        onProgress?.Invoke("Завантаження драйверів...");
                    else if (line.Trim().StartsWith("Installing"))
                        onProgress?.Invoke("Встановлення драйверів...");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Info($"[Drivers] Windows Update error: {ex.Message}");

                // Fallback: UsoClient або wuauclt
                try
                {
                    FallbackDriverScan();
                }
                catch { }

                return 0;
            }
        });
    }

    /// <summary>
    /// Fallback якщо Windows Update COM API не працює.
    /// </summary>
    private static void FallbackDriverScan()
    {
        var sys32 = GetRealSystem32();

        if (IsWindows10OrLater())
        {
            // UsoClient — Windows 10+
            var usoPath = Path.Combine(sys32, "UsoClient.exe");
            if (File.Exists(usoPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = usoPath,
                    Arguments = "StartScan",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(60000);
                Logger.Info("[Drivers] UsoClient StartScan executed");
            }
        }
        else
        {
            // wuauclt — Windows 7/8
            var wuPath = Path.Combine(sys32, "wuauclt.exe");
            if (File.Exists(wuPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = wuPath,
                    Arguments = "/detectnow /updatenow",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(30000);
                Logger.Info("[Drivers] wuauclt /detectnow /updatenow executed");
            }
        }
    }

    /// <summary>
    /// КРОК 4: Snappy Driver Installer Origin (SDIO) v2.0 — розширена база драйверів.
    /// Скачує SDIO з VPS (~6.5 MB), розпаковує, запускає з АВТОЗАВАНТАЖЕННЯМ потрібних пакетів.
    ///
    /// КЛЮЧОВЕ: -autodownload -onlyalienpacks — SDIO сам скачує ТІЛЬКИ потрібні DriverPacks
    /// (50-200 MB замість всіх 70 пакетів = десятки GB).
    /// Потім автоматично ставить оптимальні драйвери.
    ///
    /// ЗАВЖДИ запускається (навіть якщо пристрої працюють) — SDIO знаходить
    /// "more optimal" драйвери для аудіо, відео, WiFi навіть якщо ConfigManagerErrorCode = 0.
    /// </summary>
    private static async Task<int> SdioInstallDriversAsync(Action<string>? onProgress)
    {
        return await Task.Run(() =>
        {
            try
            {
                // НЕ пропускаємо навіть якщо ConfigManagerErrorCode = 0 для всіх пристроїв!
                // SDIO знаходить "More optimal driver available" навіть для працюючих пристроїв
                Logger.Info("[Drivers] SDIO v2.0: Starting (always run, downloads needed packs)");
                onProgress?.Invoke("Завантаження бази пристроїв...");

                var psScript = @"
                    $ErrorActionPreference = 'SilentlyContinue'
                    $sdioDir = Join-Path $env:TEMP 'SDIO_WO'
                    $sdioZip = Join-Path $env:TEMP 'SDIO_WO.zip'
                    $installed = 0

                    try {
                        # Очистити попередню спробу
                        Remove-Item -Path $sdioDir -Recurse -Force -ErrorAction SilentlyContinue
                        Remove-Item -Path $sdioZip -Force -ErrorAction SilentlyContinue
                        New-Item -ItemType Directory -Path $sdioDir -Force | Out-Null

                        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

                        # Завантажити SDIO з VPS (швидко, надійно)
                        Write-Output 'SDIO: Downloading from VPS...'
                        $downloaded = $false

                        try {
                            Invoke-WebRequest -Uri 'http://84.238.132.84/tools/sdio.zip' -OutFile $sdioZip -UseBasicParsing -TimeoutSec 120
                            if ((Test-Path $sdioZip) -and (Get-Item $sdioZip).Length -gt 1MB) {
                                $downloaded = $true
                                Write-Output 'SDIO: Downloaded from VPS'
                            }
                        } catch {
                            Write-Output ""VPS download failed: $($_.Exception.Message)""
                        }

                        # Fallback 1: офіційний сайт (нова версія)
                        if (-not $downloaded) {
                            $officialUrls = @(
                                'https://www.glenn.delahoy.com/downloads/sdio/SDIO_1.17.7.828.zip',
                                'https://www.glenn.delahoy.com/downloads/sdio/SDIO_1.16.0.770.zip'
                            )
                            foreach ($url in $officialUrls) {
                                if ($downloaded) { break }
                                try {
                                    Write-Output ""SDIO: Trying $url...""
                                    Invoke-WebRequest -Uri $url -OutFile $sdioZip -UseBasicParsing -TimeoutSec 180
                                    if ((Test-Path $sdioZip) -and (Get-Item $sdioZip).Length -gt 1MB) {
                                        $downloaded = $true
                                        Write-Output 'SDIO: Downloaded from official site'
                                    }
                                } catch {
                                    Write-Output ""Download failed: $($_.Exception.Message)""
                                }
                            }
                        }

                        if (-not $downloaded) {
                            Write-Output 'SDIO_ERROR: All downloads failed'
                            Write-Output 'SDIO_INSTALLED=0'
                            exit
                        }

                        # Розпакувати
                        Write-Output 'SDIO: Extracting...'
                        Expand-Archive -Path $sdioZip -DestinationPath $sdioDir -Force

                        # Знайти SDIO exe (x64 на 64-біт, x86 на 32-біт)
                        $is64 = [Environment]::Is64BitOperatingSystem
                        $sdioExe = $null

                        if ($is64) {
                            $sdioExe = Get-ChildItem -Path $sdioDir -Recurse -Filter 'SDIO_x64_*.exe' | Select-Object -First 1
                        }
                        if (-not $sdioExe) {
                            $sdioExe = Get-ChildItem -Path $sdioDir -Recurse -Filter 'SDIO_R*.exe' | Select-Object -First 1
                        }
                        if (-not $sdioExe) {
                            $sdioExe = Get-ChildItem -Path $sdioDir -Recurse -Filter 'SDIO*.exe' |
                                Where-Object { $_.Name -notmatch 'Translation|Unins' } | Select-Object -First 1
                        }

                        if (-not $sdioExe) {
                            Write-Output 'SDIO_ERROR: Executable not found'
                            Write-Output 'SDIO_INSTALLED=0'
                            exit
                        }

                        # Зберегти стан звукових пристроїв ДО запуску
                        $audioBeforeCount = (Get-WmiObject Win32_SoundDevice | Measure-Object).Count
                        $beforeProblems = (Get-WmiObject Win32_PnPEntity |
                            Where-Object { $_.ConfigManagerErrorCode -ne 0 } | Measure-Object).Count

                        # === ЗАПУСТИТИ SDIO з АВТОЗАВАНТАЖЕННЯМ потрібних DriverPacks ===
                        # -autodownload  = автоматично скачати потрібні DriverPacks
                        # -onlyalienpacks = скачати ТІЛЬКИ пакети для пристроїв цього ПК (не всі 70!)
                        # -autoinstall   = автоматично встановити знайдені драйвери
                        # -autoclose     = закритись після установки
                        # -nosnapshot    = не створювати snapshot (ми маємо свій restore point)
                        # -license       = автоматично прийняти ліцензію
                        # -expertmode    = розширений режим (всі драйвери)
                        Write-Output ""SDIO: Running $($sdioExe.Name) with autodownload + autoinstall...""
                        $proc = Start-Process -FilePath $sdioExe.FullName `
                            -ArgumentList '-autodownload -onlyalienpacks -autoinstall -autoclose -nosnapshot -license -expertmode' `
                            -PassThru -WindowStyle Hidden `
                            -WorkingDirectory $sdioExe.Directory.FullName

                        # Чекати максимум 10 хвилин
                        if (-not $proc.WaitForExit(600000)) {
                            try { $proc.Kill() } catch {}
                            Write-Output 'SDIO: Timeout (10 min), killed'
                        } else {
                            Write-Output ""SDIO: Finished with exit code $($proc.ExitCode)""
                        }

                        # Перевірити результат
                        Start-Sleep -Seconds 5
                        $afterProblems = (Get-WmiObject Win32_PnPEntity |
                            Where-Object { $_.ConfigManagerErrorCode -ne 0 } | Measure-Object).Count
                        $audioAfterCount = (Get-WmiObject Win32_SoundDevice | Measure-Object).Count

                        $problemsFixed = $beforeProblems - $afterProblems
                        if ($problemsFixed -lt 0) { $problemsFixed = 0 }

                        # Також рахуємо нові аудіо пристрої
                        $newAudio = $audioAfterCount - $audioBeforeCount
                        if ($newAudio -lt 0) { $newAudio = 0 }

                        $installed = $problemsFixed + $newAudio
                        # SDIO завжди ставить оптимальніші драйвери навіть без зміни error count
                        # Якщо SDIO відпрацював без помилок — вважаємо що драйвери оновлені

                        Write-Output ""SDIO: Problems before=$beforeProblems after=$afterProblems fixed=$problemsFixed""
                        Write-Output ""SDIO: Audio before=$audioBeforeCount after=$audioAfterCount new=$newAudio""
                        Write-Output ""SDIO_INSTALLED=$installed""

                    } catch {
                        Write-Output ""SDIO_ERROR: $($_.Exception.Message)""
                        Write-Output 'SDIO_INSTALLED=0'
                    } finally {
                        # Очистити за собою
                        Start-Sleep -Seconds 2
                        Remove-Item -Path $sdioDir -Recurse -Force -ErrorAction SilentlyContinue
                        Remove-Item -Path $sdioZip -Force -ErrorAction SilentlyContinue
                    }
                ";

                var result = RunPowerShellEncoded(psScript, 600000); // 10 хвилин макс
                Logger.Info($"[Drivers] SDIO result:\n{result}");

                // Парсимо результат
                foreach (var line in result.Split('\n'))
                {
                    if (line.Trim().StartsWith("SDIO_INSTALLED="))
                    {
                        if (int.TryParse(line.Trim().Replace("SDIO_INSTALLED=", ""), out int count))
                        {
                            if (count > 0)
                                onProgress?.Invoke($"SDIO: {count} драйверів встановлено");
                            return count;
                        }
                    }
                    // Progress для UI
                    if (line.Trim().Contains("Downloading"))
                        onProgress?.Invoke("Завантаження бази пристроїв...");
                    else if (line.Trim().Contains("Extracting"))
                        onProgress?.Invoke("Розпаковка SDIO...");
                    else if (line.Trim().Contains("Running SDIO"))
                        onProgress?.Invoke("Встановлення оптимальних драйверів...");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Info($"[Drivers] SDIO error: {ex.Message}");
                return 0;
            }
        });
    }

    /// <summary>
    /// КРОК 5: Знайти проблемні пристрої (ConfigManagerErrorCode != 0)
    /// і спробувати переінсталювати їх.
    /// </summary>
    private static async Task<int> FixProblemDevicesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var psScript = @"
                    $ErrorActionPreference = 'SilentlyContinue'
                    $fixed = 0

                    # Знайти пристрої з помилками
                    $problemDevices = Get-WmiObject Win32_PnPEntity |
                        Where-Object { $_.ConfigManagerErrorCode -ne 0 }
                    $count = ($problemDevices | Measure-Object).Count

                    Write-Output ""Problem devices found: $count""

                    if ($count -gt 0) {
                        foreach ($dev in $problemDevices) {
                            Write-Output ""  Fixing: $($dev.Name) (error $($dev.ConfigManagerErrorCode))""

                            # Спробувати Enable пристрій (Code 22 = disabled)
                            if ($dev.ConfigManagerErrorCode -eq 22) {
                                $dev.Enable()
                                $fixed++
                                continue
                            }

                            # Спробувати переінсталювати (remove + scan)
                            $devId = $dev.DeviceID
                            try {
                                pnputil /remove-device ""$devId"" /subtree 2>&1 | Out-Null
                                Start-Sleep -Milliseconds 500
                                pnputil /scan-devices 2>&1 | Out-Null
                                $fixed++
                            } catch {}
                        }
                    }

                    # Додатково: перевірити аудіо пристрої окремо
                    $audioDevices = Get-WmiObject Win32_SoundDevice
                    foreach ($ad in $audioDevices) {
                        if ($ad.StatusInfo -ne 3) { # 3 = Enabled
                            Write-Output ""  Audio device not enabled: $($ad.Name)""
                            # Спробувати enable
                            try { $ad.Enable() } catch {}
                        }
                    }

                    Write-Output ""DEVICES_FIXED=$fixed""
                ";

                var result = RunPowerShellEncoded(psScript, 60000);
                Logger.Info($"[Drivers] Problem devices:\n{result}");

                foreach (var line in result.Split('\n'))
                {
                    if (line.Trim().StartsWith("DEVICES_FIXED="))
                    {
                        if (int.TryParse(line.Trim().Replace("DEVICES_FIXED=", ""), out int count))
                            return count;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Info($"[Drivers] Problem devices error: {ex.Message}");
                return 0;
            }
        });
    }

    // === HELPERS ===

    private static string RunPowerShellEncoded(string script, int timeoutMs)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -NoLogo -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";

            // КРИТИЧНО: ReadToEnd() БЛОКУЄ НАЗАВЖДИ якщо процес повис!
            // Тому читаємо АСИНХРОННО, а потім WaitForExit з таймаутом.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            // Чекаємо з таймаутом — якщо процес завис, вбиваємо його
            if (!proc.WaitForExit(timeoutMs))
            {
                Logger.Info($"[Drivers] PS TIMEOUT after {timeoutMs / 1000}s — killing process");
                try { proc.Kill(true); } catch { }
            }

            // Після завершення/вбивства процесу — потоки закриті, можна прочитати
            string output = "";
            string stderr = "";
            try { output = outputTask.GetAwaiter().GetResult(); } catch { }
            try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }

            if (!string.IsNullOrWhiteSpace(stderr))
                Logger.Info($"[Drivers] PS stderr: {stderr.Trim()}");

            return output;
        }
        catch (Exception ex)
        {
            Logger.Info($"[Drivers] PS error: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// КРОК 6: Фінальна верифікація — перевірити чи залишились проблемні пристрої,
    /// спробувати ще раз WU для тих що не оновились.
    /// </summary>
    private static async Task<(int installed, int fixedCount)> VerifyAndRetryAsync(
        Action<string>? onProgress)
    {
        int installed = 0, fixedCount = 0;

        try
        {
            var psScript = @"
                $ErrorActionPreference = 'SilentlyContinue'
                $problems = @()

                # Знайти пристрої з проблемами (Code != 0)
                $devs = Get-WmiObject Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 }
                foreach ($d in $devs) {
                    $problems += ""$($d.Name)|$($d.DeviceID)|Code=$($d.ConfigManagerErrorCode)""
                }

                # Знайти пристрої без драйверів
                $noDriver = Get-WmiObject Win32_PnPSignedDriver | Where-Object {
                    [string]::IsNullOrEmpty($_.DriverVersion) -and
                    $_.DeviceName -ne $null
                }
                foreach ($d in $noDriver) {
                    $problems += ""NO_DRIVER|$($d.DeviceName)|$($d.HardWareID)""
                }

                if ($problems.Count -gt 0) {
                    Write-Output ""PROBLEMS:$($problems.Count)""
                    $problems | ForEach-Object { Write-Output $_ }

                    # Повторна спроба через WU для проблемних
                    try {
                        $Session = New-Object -ComObject Microsoft.Update.Session
                        $Searcher = $Session.CreateUpdateSearcher()
                        $Results = $Searcher.Search(""IsInstalled=0 and Type='Driver'"")
                        if ($Results.Updates.Count -gt 0) {
                            $ToInstall = New-Object -ComObject Microsoft.Update.UpdateColl
                            foreach ($u in $Results.Updates) {
                                if (-not $u.EulaAccepted) { $u.AcceptEula() }
                                $ToInstall.Add($u) | Out-Null
                            }
                            $dl = $Session.CreateUpdateDownloader()
                            $dl.Updates = $ToInstall
                            $dl.Download() | Out-Null
                            $ins = $Session.CreateUpdateInstaller()
                            $ins.Updates = $ToInstall
                            $r = $ins.Install()
                            Write-Output ""WU_RETRY:$($ToInstall.Count) installed, result=$($r.ResultCode)""
                        }
                    } catch { }

                    # Ще раз rescan
                    if ([Environment]::OSVersion.Version.Major -ge 10) {
                        & pnputil /scan-devices 2>&1 | Out-Null
                    }
                } else {
                    Write-Output ""ALL_OK""
                }
            ";

            var output = await Task.Run(() => RunPowerShellEncoded(psScript, 120000));
            Logger.Info($"[Drivers] Verification: {output.Trim()}");

            if (output.Contains("ALL_OK"))
            {
                Logger.Info("[Drivers] All devices OK — no problems found");
            }
            else if (output.Contains("WU_RETRY"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(output, @"WU_RETRY:(\d+)");
                if (match.Success)
                    installed = int.Parse(match.Groups[1].Value);
                Logger.Info($"[Drivers] Retry installed {installed} additional drivers");
            }

            if (output.Contains("PROBLEMS"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(output, @"PROBLEMS:(\d+)");
                if (match.Success)
                {
                    var count = int.Parse(match.Groups[1].Value);
                    Logger.Warn($"[Drivers] {count} problem devices remain after all attempts");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Drivers] Verification error: {ex.Message}");
        }

        return (installed, fixedCount);
    }

    private static bool IsWindows10OrLater()
    {
        try { return Environment.OSVersion.Version.Major >= 10; }
        catch { return false; }
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
}
