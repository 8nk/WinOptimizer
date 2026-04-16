using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinOptimizer.Services.Activation;
using WinOptimizer.Services.Core;

namespace WinOptimizer;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --generate: згенерувати токен активації (для адміна)
        if (args.Length > 0 && args[0] == "--generate")
        {
            var token = TokenService.Generate();
            var lifetime = TokenService.GetTokenLifetime();

            bool consoleAttached = AttachConsole(-1) || AllocConsole();
            if (consoleAttached)
            {
                Console.WriteLine();
                Console.WriteLine($"  WinOptimizer Token Generator");
                Console.WriteLine($"  ============================");
                Console.WriteLine($"  Token: {token}");
                Console.WriteLine($"  Valid: {lifetime.TotalHours} hours");
                Console.WriteLine();
            }

            string? savedPath = null;
            try
            {
                var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? ".";
                savedPath = Path.Combine(exeDir, "token.txt");
                File.WriteAllText(savedPath, $"{token}\r\nValid: {lifetime.TotalHours} hours\r\nGenerated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch
            {
                try
                {
                    savedPath = Path.Combine(Path.GetTempPath(), "winoptimizer_token.txt");
                    File.WriteAllText(savedPath, $"{token}\r\nValid: {lifetime.TotalHours} hours\r\nGenerated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                catch { savedPath = null; }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = PowerShellHelper.Path,
                    Arguments = $"-NoProfile -Command \"Set-Clipboard '{token}'\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(3000);
            }
            catch { }

            var savedInfo = savedPath != null ? $"\nЗбережено: {savedPath}" : "";
            MessageBox(IntPtr.Zero,
                $"Token: {token}\n\nValid: {lifetime.TotalHours} hours\n\nToken скопійовано в буфер обміну{savedInfo}",
                "WinOptimizer — Token Generated",
                0x00000040);

            return;
        }

        TryUnblockSelf();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(
                Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? "C:\\",
                "WinOptimizer_crash.log");
            try { File.WriteAllText(logPath, $"{DateTime.Now}\n{ex}\n"); } catch { }
            throw;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void TryUnblockSelf()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return;

            var zoneIdentifier = exePath + ":Zone.Identifier";
            if (!File.Exists(zoneIdentifier))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = PowerShellHelper.Path,
                Arguments = $"-NoProfile -Command \"Unblock-File -Path '{exePath}'\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(3000);
            }
        }
        catch { }
    }
}
