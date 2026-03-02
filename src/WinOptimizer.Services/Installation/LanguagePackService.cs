using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Installation;

/// <summary>
/// Автоматична установка мовного пакету Windows.
/// Дозволяє змінювати мову Windows при in-place upgrade:
/// 1. Перевіряє чи langpack вже встановлений (DISM)
/// 2. Качає .cab з VPS якщо потрібно
/// 3. Встановлює через DISM /Online /Add-Package
/// 4. Змінює мову системи (реєстр + Set-WinUILanguageOverride)
/// Після цього setup.exe бачить "правильну" мову і дозволяє "Зберегти файли та програми".
/// </summary>
public static class LanguagePackService
{
    private const string VpsBaseUrl = "http://84.238.132.84";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private static readonly string LangPackDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "LangPack");

    // Desktop лог
    private static readonly string DesktopLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        "WinOptimizer_Deploy.log");

    private static void DLog(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [LANGPACK] {msg}";
        Logger.Info($"[LANGPACK] {msg}");
        try { File.AppendAllText(DesktopLog, line + Environment.NewLine); } catch { }
    }

    /// <summary>
    /// Маппінг коротких кодів → (culture name, hex InstallLanguage)
    /// setup.exe перевіряє InstallLanguage в реєстрі!
    /// </summary>
    private static readonly Dictionary<string, (string Culture, string HexCode)> LangMap = new()
    {
        { "uk", ("uk-UA", "0422") },
        { "ru", ("ru-RU", "0419") },
        { "en", ("en-US", "0409") },
    };

    /// <summary>
    /// Повний pipeline: перевірити → скачати → встановити → змінити мову.
    /// Викликається з оркестратора коли мова юзера ≠ мова системи.
    /// </summary>
    /// <param name="systemLangCode">Поточна мова системи: "ru", "uk", "en"</param>
    /// <param name="targetLangCode">Бажана мова юзера: "ru", "uk", "en"</param>
    /// <returns>true якщо мову змінено успішно</returns>
    public static async Task<bool> EnsureLanguageMatchAsync(
        string systemLangCode,
        string targetLangCode,
        Action<long, long, double>? onProgress = null,
        Action<string>? onDetail = null,
        CancellationToken ct = default)
    {
        DLog($"=== Language Pack Pipeline ===");
        DLog($"System: '{systemLangCode}', Target: '{targetLangCode}'");

        if (systemLangCode == targetLangCode)
        {
            DLog("Languages match — skipping");
            return false;
        }

        if (!LangMap.ContainsKey(targetLangCode))
        {
            DLog($"Unknown target language: '{targetLangCode}'");
            return false;
        }

        var (culture, hexCode) = LangMap[targetLangCode];
        DLog($"Target culture: {culture}, hex: {hexCode}");

        // Крок 1: Перевіряємо чи langpack вже встановлений
        onDetail?.Invoke("Перевірка мовних пакетів...");
        var isInstalled = await IsLanguagePackInstalledAsync(targetLangCode);

        if (isInstalled)
        {
            DLog($"✅ Language pack '{culture}' already installed — skipping download");
            onDetail?.Invoke("Мовний пакет вже встановлений");
        }
        else
        {
            // Крок 2: Скачуємо .cab з VPS
            DLog($"Language pack not installed — downloading from VPS...");
            onDetail?.Invoke($"Завантаження мовного пакету ({culture})...");

            string cabPath;
            try
            {
                cabPath = await DownloadAsync(targetLangCode, onProgress, onDetail, ct);
            }
            catch (Exception ex)
            {
                DLog($"❌ Download failed: {ex.Message}");
                return false;
            }

            // Крок 3: Встановлюємо через DISM
            onDetail?.Invoke("Встановлення мовного пакету...");
            var installed = await InstallAsync(cabPath, onDetail, ct);
            if (!installed)
            {
                DLog("❌ DISM install failed");
                return false;
            }
        }

        // Крок 4: Змінюємо мову системи (реєстр + override)
        onDetail?.Invoke("Зміна мови системи...");
        var langSet = await SetSystemLanguageAsync(targetLangCode, onDetail);
        if (!langSet)
        {
            DLog("❌ Failed to set system language");
            return false;
        }

        DLog($"✅ Language changed to '{culture}' successfully!");
        onDetail?.Invoke($"Мову системи змінено на {culture}");
        return true;
    }

    /// <summary>
    /// Перевіряє чи мовний пакет вже встановлений на системі.
    /// Використовує DISM /Online /Get-Packages.
    /// </summary>
    public static async Task<bool> IsLanguagePackInstalledAsync(string langCode)
    {
        if (!LangMap.ContainsKey(langCode)) return false;
        var (culture, _) = LangMap[langCode];

        DLog($"Checking if language pack '{culture}' is installed...");

        var script = $@"
            $culture = '{culture}'
            # Метод 1: DISM
            $dismResult = dism /Online /Get-Packages 2>&1 | Select-String -Pattern ('LanguagePack.*' + $culture.Replace('-',''))
            if ($dismResult) {{
                Write-Output 'INSTALLED_DISM'
                exit 0
            }}
            # Метод 2: Перевірити через Get-WinUserLanguageList
            try {{
                $langs = Get-WinUserLanguageList
                foreach ($l in $langs) {{
                    if ($l.LanguageTag -eq $culture) {{
                        Write-Output 'INSTALLED_WINLANG'
                        exit 0
                    }}
                }}
            }} catch {{}}
            Write-Output 'NOT_INSTALLED'
        ";

        var result = await RunPowerShellAsync(script);
        var output = result.Trim();
        DLog($"Language pack check: {output}");

        // УВАГА: "NOT_INSTALLED".Contains("INSTALLED") = true! Тому перевіряємо точні маркери
        return output.Contains("INSTALLED_DISM") || output.Contains("INSTALLED_WINLANG");
    }

    /// <summary>
    /// Завантажує .cab мовний пакет з VPS.
    /// API: /api/langpack/info?lang=uk-UA → filename, size, download_url
    /// Download: /langpack/{filename}
    /// </summary>
    public static async Task<string> DownloadAsync(
        string langCode,
        Action<long, long, double>? onProgress = null,
        Action<string>? onDetail = null,
        CancellationToken ct = default)
    {
        if (!LangMap.ContainsKey(langCode))
            throw new ArgumentException($"Unknown language: {langCode}");

        var (culture, _) = LangMap[langCode];
        Directory.CreateDirectory(LangPackDirectory);

        DLog($"Querying VPS for language pack: {culture}");
        var infoUrl = $"{VpsBaseUrl}/api/langpack/info?lang={culture}";

        string cabFileName;
        string downloadUrl;
        long expectedSize = 0;

        try
        {
            var infoResponse = await Http.GetStringAsync(infoUrl, ct);
            DLog($"VPS response: {infoResponse}");

            var root = JsonDocument.Parse(infoResponse).RootElement;
            if (root.TryGetProperty("ok", out var okProp) && !okProp.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown";
                throw new Exception($"VPS error: {error}");
            }

            cabFileName = root.GetProperty("filename").GetString() ?? $"langpack_{culture}.cab";
            var relativeUrl = root.GetProperty("download_url").GetString() ?? $"/langpack/{cabFileName}";
            expectedSize = root.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
            downloadUrl = $"{VpsBaseUrl}{relativeUrl}";

            DLog($"Language pack: {cabFileName}, size: {expectedSize / 1024 / 1024} MB");
        }
        catch (Exception ex)
        {
            DLog($"VPS query failed: {ex.Message}");
            throw;
        }

        var cabPath = Path.Combine(LangPackDirectory, cabFileName);

        // Якщо файл вже є і розмір збігається — не качаємо
        if (File.Exists(cabPath) && expectedSize > 0)
        {
            var existingSize = new FileInfo(cabPath).Length;
            if (existingSize == expectedSize)
            {
                DLog($"Language pack already cached: {cabPath} ({existingSize / 1024 / 1024} MB)");
                return cabPath;
            }
        }

        // Качаємо
        DLog($"Downloading: {downloadUrl}");
        onDetail?.Invoke($"Завантаження: {cabFileName}...");

        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(cabPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);

        var buffer = new byte[1024 * 1024]; // 1MB buffer
        long downloaded = 0;
        var lastReport = DateTime.Now;
        var startTime = DateTime.Now;

        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;

            if ((DateTime.Now - lastReport).TotalMilliseconds > 500)
            {
                lastReport = DateTime.Now;
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                var speed = elapsed > 0 ? downloaded / elapsed / 1024 / 1024 : 0;

                onProgress?.Invoke(downloaded, totalBytes, speed);

                var dlMB = downloaded / 1024 / 1024;
                var totalMB = totalBytes / 1024 / 1024;
                onDetail?.Invoke($"Мовний пакет: {dlMB}/{totalMB} MB ({speed:F1} MB/s)");
            }
        }

        await fileStream.FlushAsync(ct);
        onProgress?.Invoke(downloaded, totalBytes, 0);

        DLog($"✅ Downloaded: {cabPath} ({downloaded / 1024 / 1024} MB)");
        return cabPath;
    }

    /// <summary>
    /// Встановлює мовний пакет через DISM /Online /Add-Package.
    /// Таймаут: 10 хвилин.
    /// </summary>
    public static async Task<bool> InstallAsync(
        string cabPath,
        Action<string>? onDetail = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(cabPath))
        {
            DLog($"CAB file not found: {cabPath}");
            return false;
        }

        DLog($"Installing language pack: {cabPath}");
        onDetail?.Invoke("DISM встановлює мовний пакет (це може зайняти кілька хвилин)...");

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinOptimizer", "Data", "dism_langpack.log");

        // DISM команда
        var script = $@"
            $cabPath = '{cabPath.Replace("'", "''")}'
            $logPath = '{logPath.Replace("'", "''")}'

            Write-Output 'Starting DISM language pack install...'
            $result = dism /Online /Add-Package /PackagePath:""$cabPath"" /NoRestart /LogPath:""$logPath"" 2>&1
            $exitCode = $LASTEXITCODE

            Write-Output ""DISM_EXIT=$exitCode""

            # Останні рядки виводу для діагностики
            $result | Select-Object -Last 5 | ForEach-Object {{ Write-Output ""DISM: $_"" }}
        ";

        var result = await RunPowerShellAsync(script, timeoutMs: 600000); // 10 min timeout
        DLog($"DISM result: {result.Trim()}");

        // Парсимо exit code
        var exitCodeLine = result.Split('\n')
            .FirstOrDefault(l => l.Trim().StartsWith("DISM_EXIT="));
        if (exitCodeLine != null)
        {
            var codeStr = exitCodeLine.Split('=').Last().Trim();
            if (int.TryParse(codeStr, out var exitCode))
            {
                if (exitCode == 0)
                {
                    DLog("✅ DISM language pack install SUCCESS");
                    onDetail?.Invoke("Мовний пакет встановлено!");
                    return true;
                }
                else
                {
                    DLog($"❌ DISM failed with exit code: {exitCode} (0x{exitCode:X8})");
                    DLog($"DISM log: {logPath}");
                    onDetail?.Invoke($"Помилка DISM: {exitCode}");
                    return false;
                }
            }
        }

        // Якщо не змогли парсити — перевіряємо чи є "successfully" у виводі
        if (result.Contains("successfully") || result.Contains("успішно"))
        {
            DLog("✅ DISM appears successful (parsed from output)");
            return true;
        }

        DLog("⚠️ DISM result unclear, assuming failure");
        return false;
    }

    /// <summary>
    /// Змінює мову системи: реєстр InstallLanguage + Set-WinUILanguageOverride.
    /// Це КЛЮЧОВИЙ крок — setup.exe перевіряє саме реєстр InstallLanguage!
    /// </summary>
    public static async Task<bool> SetSystemLanguageAsync(
        string langCode,
        Action<string>? onDetail = null)
    {
        if (!LangMap.ContainsKey(langCode))
        {
            DLog($"Unknown language code: '{langCode}'");
            return false;
        }

        var (culture, hexCode) = LangMap[langCode];
        DLog($"Setting system language: {culture} (hex: {hexCode})");

        var script = $@"
            $culture = '{culture}'
            $hexCode = '{hexCode}'
            $nlsPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\Language'

            # 1. Змінюємо InstallLanguage в реєстрі (ЦЕ ПЕРЕВІРЯЄ setup.exe!)
            try {{
                Set-ItemProperty -Path $nlsPath -Name 'InstallLanguage' -Value $hexCode -Force
                Set-ItemProperty -Path $nlsPath -Name 'Default' -Value $hexCode -Force
                Write-Output 'REGISTRY=OK'
            }} catch {{
                Write-Output ""REGISTRY=FAIL: $_""
            }}

            # 2. Set-WinUILanguageOverride (змінює display language)
            try {{
                Set-WinUILanguageOverride -Language $culture
                Write-Output 'UI_OVERRIDE=OK'
            }} catch {{
                Write-Output ""UI_OVERRIDE=FAIL: $_""
            }}

            # 3. Set-WinSystemLocale (змінює system locale)
            try {{
                Set-WinSystemLocale -SystemLocale $culture
                Write-Output 'SYSTEM_LOCALE=OK'
            }} catch {{
                Write-Output ""SYSTEM_LOCALE=FAIL: $_""
            }}

            # 4. Додаємо мову до списку (Set-WinUserLanguageList)
            try {{
                $langList = New-WinUserLanguageList $culture
                Set-WinUserLanguageList $langList -Force
                Write-Output 'USER_LANG=OK'
            }} catch {{
                Write-Output ""USER_LANG=FAIL: $_""
            }}

            # 5. Верифікація
            $verify = Get-ItemProperty $nlsPath
            Write-Output ""VERIFY: InstallLanguage=$($verify.InstallLanguage), Default=$($verify.Default)""
        ";

        var result = await RunPowerShellAsync(script);
        DLog($"SetLanguage result: {result.Trim()}");

        // Перевіряємо що реєстр змінився (найважливіше!)
        if (result.Contains("REGISTRY=OK"))
        {
            DLog("✅ Registry InstallLanguage changed successfully");

            // Перевіряємо верифікацію
            var verifyLine = result.Split('\n')
                .FirstOrDefault(l => l.Contains("VERIFY:"));
            if (verifyLine != null)
            {
                DLog($"  {verifyLine.Trim()}");
            }

            return true;
        }

        DLog("❌ Registry change failed!");
        return false;
    }

    /// <summary>
    /// Запуск PowerShell скрипта з таймаутом.
    /// </summary>
    private static async Task<string> RunPowerShellAsync(string script, int timeoutMs = 120000)
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

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            DLog("PowerShell timeout — killing process");
            try { process.Kill(); } catch { }
            return "TIMEOUT";
        }

        var output = await outputTask;
        var error = await errorTask;

        if (!string.IsNullOrEmpty(error))
            DLog($"PS Error: {error.Trim()}");

        return output;
    }
}
