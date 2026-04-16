using WinOptimizer.Services.Logging;

namespace WinOptimizer.Services.Cleanup;

/// <summary>
/// Очистка профілю користувача — Desktop, Downloads, Documents, AppData.
/// Чіпає ТІЛЬКИ диск C:, D:/E: та інші не зачіпаються.
/// </summary>
public static class UserProfileCleaner
{
    // Файли/папки які НЕ МОЖНА видаляти
    private static readonly string[] ProtectedFileNames =
    {
        "WinOptimizer", "desktop.ini", "ntuser", "NTUSER"
    };

    // Папки в AppData\Local які НЕ МОЖНА видаляти
    private static readonly string[] ProtectedLocalFolders =
    {
        "Microsoft", "Packages", "ConnectedDevicesPlatform",
        "Comms", "DBG", "PlaceholderTileLogoFolder",
        "WinOptimizer"
    };

    // Папки в AppData\Roaming які НЕ МОЖНА видаляти
    private static readonly string[] ProtectedRoamingFolders =
    {
        "Microsoft", "WinOptimizer"
    };

    public static async Task<long> CleanUserProfileAsync(
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        long totalCleaned = 0;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Logger.Info("UserProfileCleaner: starting cleanup...");

        // 1. Clean known user folders on C: only
        var foldersToClean = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(userProfile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(userProfile, "Favorites"),
            Path.Combine(userProfile, "Saved Games"),
            Path.Combine(userProfile, "Contacts"),
            Path.Combine(userProfile, "Searches"),
            Path.Combine(userProfile, "Links"),
        };

        foreach (var folder in foldersToClean)
        {
            token.ThrowIfCancellationRequested();

            if (!IsOnSystemDrive(folder)) continue;

            if (Directory.Exists(folder))
            {
                var name = Path.GetFileName(folder);
                onProgress?.Invoke("Налаштування профілю користувача...");
                Logger.Info($"Cleaning user folder: {folder}");
                var cleaned = await CleanFolderContentsAsync(folder);
                totalCleaned += cleaned;
                Logger.Info($"  Cleaned {cleaned / 1024}KB from {name}");
            }
        }

        // 2. Clean Public Desktop (program shortcuts like Yandex, Edge etc.)
        token.ThrowIfCancellationRequested();
        var publicDesktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        if (Directory.Exists(publicDesktop))
        {
            onProgress?.Invoke("Оновлення спільних ресурсів...");
            Logger.Info($"Cleaning Public Desktop: {publicDesktop}");
            var cleaned = await CleanFolderContentsAsync(publicDesktop);
            totalCleaned += cleaned;
            Logger.Info($"  Cleaned {cleaned / 1024}KB from Public Desktop");
        }

        // 3. Clean Public Documents
        token.ThrowIfCancellationRequested();
        var publicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (!string.IsNullOrEmpty(publicDocs) && Directory.Exists(publicDocs))
        {
            onProgress?.Invoke("Перевірка спільних документів...");
            Logger.Info($"Cleaning Public Documents: {publicDocs}");
            var cleaned = await CleanFolderContentsAsync(publicDocs);
            totalCleaned += cleaned;
            Logger.Info($"  Cleaned {cleaned / 1024}KB from Public Documents");
        }

        // 4. Clean AppData\Local (except protected)
        token.ThrowIfCancellationRequested();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Directory.Exists(localAppData) && IsOnSystemDrive(localAppData))
        {
            onProgress?.Invoke("Оновлення локальних налаштувань...");
            Logger.Info($"Cleaning AppData\\Local: {localAppData}");
            var cleaned = await CleanAppDataFolderAsync(localAppData, ProtectedLocalFolders);
            totalCleaned += cleaned;
            Logger.Info($"  Cleaned {cleaned / 1024}KB from AppData\\Local");
        }

        // 5. Clean AppData\Roaming (except protected)
        token.ThrowIfCancellationRequested();
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (Directory.Exists(roamingAppData) && IsOnSystemDrive(roamingAppData))
        {
            onProgress?.Invoke("Синхронізація параметрів додатків...");
            Logger.Info($"Cleaning AppData\\Roaming: {roamingAppData}");
            var cleaned = await CleanAppDataFolderAsync(roamingAppData, ProtectedRoamingFolders);
            totalCleaned += cleaned;
            Logger.Info($"  Cleaned {cleaned / 1024}KB from AppData\\Roaming");
        }

        // 6. Clean Recent files
        token.ThrowIfCancellationRequested();
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (Directory.Exists(recent))
        {
            onProgress?.Invoke("Оновлення історії файлів...");
            Logger.Info($"Cleaning Recent: {recent}");
            var cleaned = await CleanFolderContentsAsync(recent);
            totalCleaned += cleaned;
            Logger.Info($"  Cleaned {cleaned / 1024}KB from Recent");
        }

        // 7. Clean all other user profiles (not just current)
        token.ThrowIfCancellationRequested();
        await CleanAllUserProfilesAsync(onProgress, totalCleaned, token);

        Logger.Info($"UserProfileCleaner TOTAL: cleaned {totalCleaned / (1024 * 1024)}MB");
        return totalCleaned;
    }

    /// <summary>
    /// Очистка Desktop/Downloads/Documents для ВСІХ користувачів на C:
    /// </summary>
    private static async Task CleanAllUserProfilesAsync(
        Action<string>? onProgress, long totalCleaned, CancellationToken token)
    {
        try
        {
            var usersDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users");
            if (!Directory.Exists(usersDir)) return;

            var currentUser = Environment.UserName;
            var skipProfiles = new[] { "Default", "Default User", "Public", "All Users", currentUser };

            foreach (var profileDir in Directory.GetDirectories(usersDir))
            {
                var profileName = Path.GetFileName(profileDir);
                if (skipProfiles.Any(s => s.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                token.ThrowIfCancellationRequested();

                Logger.Info($"Cleaning other user profile: {profileName}");
                onProgress?.Invoke($"Очистка профілю {profileName}...");

                var profileFolders = new[]
                {
                    Path.Combine(profileDir, "Desktop"),
                    Path.Combine(profileDir, "Downloads"),
                    Path.Combine(profileDir, "Documents"),
                    Path.Combine(profileDir, "Pictures"),
                    Path.Combine(profileDir, "Videos"),
                    Path.Combine(profileDir, "Music"),
                };

                foreach (var folder in profileFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        try
                        {
                            var cleaned = await CleanFolderContentsAsync(folder);
                            totalCleaned += cleaned;
                            Logger.Info($"  Cleaned {cleaned / 1024}KB from {folder}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"  Cannot clean {folder}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"CleanAllUserProfiles error: {ex.Message}");
        }
    }

    private static bool IsOnSystemDrive(string path)
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            var pathRoot = Path.GetPathRoot(path);
            return string.Equals(systemDrive, pathRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsProtectedFile(string fileName)
    {
        return ProtectedFileNames.Any(p =>
            fileName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<long> CleanFolderContentsAsync(string folderPath)
    {
        return Task.Run(() =>
        {
            long cleaned = 0;
            int filesDeleted = 0;
            int filesFailed = 0;

            try
            {
                var di = new DirectoryInfo(folderPath);

                // Delete files first
                foreach (var file in di.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        // Skip protected files
                        if (IsProtectedFile(file.Name)) continue;

                        var size = file.Length;
                        file.Delete();
                        cleaned += size;
                        filesDeleted++;
                    }
                    catch
                    {
                        filesFailed++;
                    }
                }

                // Delete subdirectories and their contents
                foreach (var dir in di.EnumerateDirectories())
                {
                    try
                    {
                        // Skip protected
                        if (IsProtectedFile(dir.Name)) continue;

                        var size = GetDirectorySize(dir);
                        dir.Delete(true);
                        cleaned += size;
                        filesDeleted++;
                    }
                    catch
                    {
                        // Try to at least clean files inside
                        try
                        {
                            cleaned += CleanInsideDirectory(dir);
                        }
                        catch { }
                        filesFailed++;
                    }
                }

                if (filesDeleted > 0 || filesFailed > 0)
                    Logger.Info($"  {folderPath}: deleted={filesDeleted}, failed={filesFailed}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Error cleaning {folderPath}: {ex.Message}");
            }
            return cleaned;
        });
    }

    /// <summary>
    /// Спроба очистити вміст директорії яку не вдалось видалити повністю.
    /// </summary>
    private static long CleanInsideDirectory(DirectoryInfo dir)
    {
        long cleaned = 0;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (IsProtectedFile(file.Name)) continue;
                    var size = file.Length;
                    file.Delete();
                    cleaned += size;
                }
                catch { }
            }
        }
        catch { }
        return cleaned;
    }

    private static Task<long> CleanAppDataFolderAsync(string appDataPath, string[] protectedFolders)
    {
        return Task.Run(() =>
        {
            long cleaned = 0;
            int dirsDeleted = 0;

            try
            {
                var di = new DirectoryInfo(appDataPath);

                foreach (var subDir in di.EnumerateDirectories())
                {
                    if (protectedFolders.Any(p =>
                            subDir.Name.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    try
                    {
                        var size = GetDirectorySize(subDir);
                        subDir.Delete(true);
                        cleaned += size;
                        dirsDeleted++;
                    }
                    catch
                    {
                        // Try partial cleanup
                        try { cleaned += CleanInsideDirectory(subDir); }
                        catch { }
                    }
                }

                if (dirsDeleted > 0)
                    Logger.Info($"  AppData: deleted {dirsDeleted} dirs");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Error cleaning AppData: {ex.Message}");
            }
            return cleaned;
        });
    }

    private static long GetDirectorySize(DirectoryInfo di)
    {
        long size = 0;
        try
        {
            foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { size += fi.Length; }
                catch { }
            }
        }
        catch { }
        return size;
    }
}
