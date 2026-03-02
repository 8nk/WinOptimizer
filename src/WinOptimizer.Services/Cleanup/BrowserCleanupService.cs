using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Очистка кешу браузерів: Chrome, Firefox, Edge.
/// </summary>
public static class BrowserCleanupService
{
    public static async Task<long> CleanAsync(Action<string>? onProgress = null)
    {
        long totalCleaned = 0;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Chrome
        onProgress?.Invoke("Очистка кешу Chrome...");
        totalCleaned += await Task.Run(() => CleanBrowser("Chrome", new[]
        {
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Cache"),
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Code Cache"),
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\GPUCache"),
        }));

        // Edge
        onProgress?.Invoke("Очистка кешу Edge...");
        totalCleaned += await Task.Run(() => CleanBrowser("Edge", new[]
        {
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Cache"),
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Code Cache"),
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\GPUCache"),
        }));

        // Firefox
        onProgress?.Invoke("Очистка кешу Firefox...");
        totalCleaned += await Task.Run(() => CleanFirefox(appData));

        Logger.Info($"Total browser cleanup: {totalCleaned} bytes");
        return totalCleaned;
    }

    private static long CleanBrowser(string name, string[] cachePaths)
    {
        long cleaned = 0;
        foreach (var path in cachePaths)
        {
            try
            {
                if (!Directory.Exists(path)) continue;
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
        }
        Logger.Info($"{name} cache cleaned: {cleaned} bytes");
        return cleaned;
    }

    private static long CleanFirefox(string appData)
    {
        long cleaned = 0;
        try
        {
            var profilesDir = Path.Combine(appData, @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesDir)) return 0;

            foreach (var profile in Directory.EnumerateDirectories(profilesDir))
            {
                var cache2 = Path.Combine(profile, "cache2");
                if (!Directory.Exists(cache2)) continue;

                foreach (var file in Directory.EnumerateFiles(cache2, "*", SearchOption.AllDirectories))
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
        }
        catch { }
        Logger.Info($"Firefox cache cleaned: {cleaned} bytes");
        return cleaned;
    }
}
