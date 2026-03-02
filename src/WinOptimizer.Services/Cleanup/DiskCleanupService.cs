using System.Diagnostics;
using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Очистка диска: temp файли, кеш, кошик, Windows логи.
/// </summary>
public static class DiskCleanupService
{
    public static async Task<long> CleanAsync(Action<string>? onProgress = null)
    {
        long totalCleaned = 0;

        // 1. Temp files
        onProgress?.Invoke("Очистка тимчасових файлів...");
        totalCleaned += await Task.Run(() => CleanDirectory(Path.GetTempPath()));
        totalCleaned += await Task.Run(() => CleanDirectory(@"C:\Windows\Temp"));
        totalCleaned += await Task.Run(() => CleanDirectory(@"C:\Windows\Prefetch"));
        Logger.Info($"Temp cleaned: {totalCleaned} bytes");

        // 2. Recycle Bin
        onProgress?.Invoke("Очистка кошика...");
        totalCleaned += await Task.Run(() => ClearRecycleBin());
        Logger.Info($"After recycle bin: {totalCleaned} bytes total");

        // 3. Windows logs
        onProgress?.Invoke("Очистка логів Windows...");
        totalCleaned += await Task.Run(() => CleanDirectory(@"C:\Windows\Logs"));

        // 4. Thumbnails
        onProgress?.Invoke("Очистка кешу мініатюр...");
        var thumbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Explorer");
        totalCleaned += await Task.Run(() => CleanFilesByPattern(thumbPath, "thumbcache_*.db"));

        // 5. DISM cleanup
        onProgress?.Invoke("DISM очистка компонентів...");
        await Task.Run(() => RunDismCleanup());

        Logger.Info($"Total disk cleanup: {totalCleaned} bytes");
        return totalCleaned;
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
                catch { } // File in use — skip
            }

            // Clean empty subdirectories
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Reverse())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"CleanDirectory {path}", ex);
        }
        return cleaned;
    }

    private static long CleanFilesByPattern(string path, string pattern)
    {
        long cleaned = 0;
        try
        {
            if (!Directory.Exists(path)) return 0;
            foreach (var file in Directory.EnumerateFiles(path, pattern))
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

    private static long ClearRecycleBin()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Clear-RecycleBin -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            return 0; // Розмір вже порахований в scan
        }
        catch { return 0; }
    }

    private static void RunDismCleanup()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "DISM.exe",
                Arguments = "/Online /Cleanup-Image /StartComponentCleanup /ResetBase",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(120000); // 2 min max
            Logger.Info("DISM cleanup completed");
        }
        catch (Exception ex)
        {
            Logger.Error("DISM cleanup failed", ex);
        }
    }
}
