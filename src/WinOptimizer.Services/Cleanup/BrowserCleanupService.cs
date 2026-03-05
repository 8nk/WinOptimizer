using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Очистка кешу браузерів: Chrome, Firefox, Edge, Yandex, Opera, Brave.
/// Підтримує ВСІ профілі (не тільки Default).
/// </summary>
public static class BrowserCleanupService
{
    public static async Task<long> CleanAsync(Action<string>? onProgress = null)
    {
        long totalCleaned = 0;

        var usersDir = @"C:\Users";
        if (!Directory.Exists(usersDir)) return 0;

        foreach (var userDir in Directory.GetDirectories(usersDir))
        {
            var userName = Path.GetFileName(userDir).ToLowerInvariant();
            if (userName is "public" or "default" or "default user" or "all users") continue;

            var localAppData = Path.Combine(userDir, "AppData", "Local");
            var roamingAppData = Path.Combine(userDir, "AppData", "Roaming");

            // Chrome (всі профілі: Default, Profile 1, Profile 2...)
            onProgress?.Invoke("Очистка кешу Chrome...");
            totalCleaned += await Task.Run(() =>
                CleanBrowserProfiles("Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data")));

            // Edge (всі профілі)
            onProgress?.Invoke("Очистка кешу Edge...");
            totalCleaned += await Task.Run(() =>
                CleanBrowserProfiles("Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data")));

            // Yandex Browser
            onProgress?.Invoke("Очистка кешу Yandex...");
            totalCleaned += await Task.Run(() =>
                CleanBrowserProfiles("Yandex", Path.Combine(localAppData, "Yandex", "YandexBrowser", "User Data")));

            // Brave
            totalCleaned += await Task.Run(() =>
                CleanBrowserProfiles("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")));

            // Opera
            onProgress?.Invoke("Очистка кешу Opera...");
            totalCleaned += await Task.Run(() =>
            {
                var operaDir = Path.Combine(roamingAppData, "Opera Software", "Opera Stable");
                return CleanCacheDirs("Opera", operaDir);
            });

            // Firefox (всі профілі)
            onProgress?.Invoke("Очистка кешу Firefox...");
            totalCleaned += await Task.Run(() => CleanFirefox(roamingAppData));
        }

        Logger.Info($"Total browser cleanup: {totalCleaned} bytes ({totalCleaned / (1024.0 * 1024):F1} MB)");
        return totalCleaned;
    }

    /// <summary>
    /// Очистка всіх профілів Chromium-based браузера.
    /// </summary>
    private static long CleanBrowserProfiles(string name, string userDataDir)
    {
        if (!Directory.Exists(userDataDir)) return 0;
        long total = 0;

        foreach (var profileDir in Directory.GetDirectories(userDataDir))
        {
            total += CleanCacheDirs(name, profileDir);
        }

        Logger.Info($"{name} total cache cleaned: {total} bytes");
        return total;
    }

    private static long CleanCacheDirs(string name, string profileDir)
    {
        if (!Directory.Exists(profileDir)) return 0;
        long cleaned = 0;

        // Standard cache directories
        string[] cacheDirs =
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache",
            "ShaderCache", "GrShaderCache",
            Path.Combine("Service Worker", "CacheStorage"),
            Path.Combine("Service Worker", "ScriptCache"),
        };

        foreach (var cacheDir in cacheDirs)
        {
            var fullPath = Path.Combine(profileDir, cacheDir);
            cleaned += CleanDirectory(fullPath);
        }

        // Cache files by extension
        try
        {
            foreach (var file in Directory.EnumerateFiles(profileDir, "*.tmp"))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                }
                catch { }
            }
        }
        catch { }

        return cleaned;
    }

    private static long CleanFirefox(string appData)
    {
        long cleaned = 0;
        try
        {
            var profilesDir = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
            if (!Directory.Exists(profilesDir)) return 0;

            foreach (var profile in Directory.EnumerateDirectories(profilesDir))
            {
                cleaned += CleanDirectory(Path.Combine(profile, "cache2"));
                cleaned += CleanDirectory(Path.Combine(profile, "thumbnails"));
                cleaned += CleanDirectory(Path.Combine(profile, "startupCache"));
                cleaned += CleanDirectory(Path.Combine(profile, "shader-cache"));
            }
        }
        catch { }
        Logger.Info($"Firefox cache cleaned: {cleaned} bytes");
        return cleaned;
    }

    private static long CleanDirectory(string path)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned += size;
                }
                catch { }
            }
        }
        catch { }
        return cleaned;
    }
}
