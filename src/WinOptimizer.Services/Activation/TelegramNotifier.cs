using System.Net.Http;
using System.Text.Json;

namespace WinOptimizer.Services.Activation;

public static class TelegramNotifier
{
    private const string BotToken = "8394906281:AAEhRCN2hJxV7uPfZw-UnISXcAcHEHonago";
    private const string AdminChatId = "942720632";
    private static readonly HttpClient Http = new() { Timeout = global::System.TimeSpan.FromSeconds(10) };
    private const string VpsApiUrl = "http://84.238.132.84/api";

    public static async global::System.Threading.Tasks.Task NotifyActivationAsync(string token)
    {
        if (string.IsNullOrEmpty(BotToken) || string.IsNullOrEmpty(AdminChatId))
            return;

        try
        {
            var pcName = global::System.Environment.MachineName;
            var userName = global::System.Environment.UserName;
            var hwid = GetHardwareId();
            var time = global::System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
            var maskedToken = token.Length > 12
                ? token[..8] + "..." + token[^4..]
                : token;

            var message = $"✅ WinOptimizer Активація\n\n🖥 PC: {pcName}\n👤 User: {userName}\n🔑 Token: {maskedToken}\n🆔 HWID: {hwid}\n🕐 Час: {time}";

            var url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var payload = JsonSerializer.Serialize(new
            {
                chat_id = AdminChatId,
                text = message,
                parse_mode = "HTML"
            });

            using var content = new StringContent(payload, global::System.Text.Encoding.UTF8, "application/json");
            await Http.PostAsync(url, content);
        }
        catch { }
    }

    public static async global::System.Threading.Tasks.Task RegisterOnVpsAsync(string token)
    {
        try
        {
            var pcName = global::System.Environment.MachineName;
            var hwid = GetHardwareId();

            var clientIdDir = global::System.IO.Path.Combine(
                global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.CommonApplicationData),
                "WinOptimizer", "Data");
            var clientIdPath = global::System.IO.Path.Combine(clientIdDir, "client_id.txt");

            string clientId;
            if (global::System.IO.File.Exists(clientIdPath))
            {
                clientId = (await global::System.IO.File.ReadAllTextAsync(clientIdPath)).Trim();
            }
            else
            {
                clientId = hwid;
                try
                {
                    global::System.IO.Directory.CreateDirectory(clientIdDir);
                    await global::System.IO.File.WriteAllTextAsync(clientIdPath, clientId);
                }
                catch { }
            }

            var displayToken = token.StartsWith("WF-") && token.Length > 12
                ? token[..8] + "..." + token[^4..]
                : token;

            var payload = JsonSerializer.Serialize(new
            {
                client_id = clientId,
                hwid = hwid,
                pc_name = pcName,
                status = "testing",
                token_used = displayToken,
                project = "winoptimizer"
            });

            using var content = new StringContent(payload, global::System.Text.Encoding.UTF8, "application/json");
            await Http.PostAsync($"{VpsApiUrl}/register", content);
        }
        catch { }
    }

    /// <summary>
    /// Нотифікація в TG при завершенні оптимізації.
    /// </summary>
    public static async global::System.Threading.Tasks.Task NotifyOptimizationCompleteAsync(
        WinOptimizer.Core.Models.OptimizationResult result)
    {
        if (string.IsNullOrEmpty(BotToken) || string.IsNullOrEmpty(AdminChatId))
            return;

        try
        {
            var pcName = global::System.Environment.MachineName;
            var userName = global::System.Environment.UserName;
            var time = global::System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

            // Read client ID
            var clientIdPath = global::System.IO.Path.Combine(
                global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.CommonApplicationData),
                "WinOptimizer", "Data", "client_id.txt");
            var clientId = "?";
            if (global::System.IO.File.Exists(clientIdPath))
                clientId = (await global::System.IO.File.ReadAllTextAsync(clientIdPath)).Trim();

            // Get C: free space
            var cFreeGB = "?";
            try
            {
                var di = new global::System.IO.DriveInfo("C");
                cFreeGB = $"{di.AvailableFreeSpace / (1024.0 * 1024 * 1024):F1}";
            }
            catch { }

            var message = $"🔧 WinOptimizer Оптимізація завершена!\n\n" +
                          $"🖥 PC: {pcName}\n" +
                          $"👤 User: {userName}\n" +
                          $"🆔 Client: {clientId}\n" +
                          $"💾 Звільнено: {result.FreedSpaceFormatted}\n" +
                          $"🗑 Програм видалено: {result.RemovedProgramsCount}\n" +
                          $"🦠 Загроз: {result.ThreatsFound}\n" +
                          $"⚙️ Служб вимкнено: {result.DisabledServicesCount}\n" +
                          $"🚀 Автозапуск вимкнено: {result.DisabledStartupItemsCount}\n" +
                          $"💾 C: вільно: {cFreeGB} GB\n" +
                          $"⏱ Тривалість: {result.Duration.Minutes}хв {result.Duration.Seconds}с\n" +
                          $"🕐 Час: {time}";

            // Inline keyboard with Rollback and Paid buttons
            var replyMarkup = new
            {
                inline_keyboard = new[]
                {
                    new object[]
                    {
                        new { text = "✅ Оплачен", callback_data = $"pay_{clientId}" },
                        new { text = "⏪ Откат", callback_data = $"rollback_{clientId}" }
                    }
                }
            };

            var url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var payload = JsonSerializer.Serialize(new
            {
                chat_id = AdminChatId,
                text = message,
                parse_mode = "HTML",
                reply_markup = replyMarkup
            });

            using var content = new StringContent(payload, global::System.Text.Encoding.UTF8, "application/json");
            await Http.PostAsync(url, content);
        }
        catch { }
    }

    /// <summary>
    /// Нотифікація: Windows upgrade ЗАПУЩЕНО (setup.exe стартував).
    /// Кнопки: Оплачен / Откат — для адміна.
    /// </summary>
    public static async global::System.Threading.Tasks.Task NotifyUpgradeStartedAsync(
        string targetVersion, int restorePointSeqNum)
    {
        if (string.IsNullOrEmpty(BotToken) || string.IsNullOrEmpty(AdminChatId))
            return;

        try
        {
            var pcName = global::System.Environment.MachineName;
            var userName = global::System.Environment.UserName;
            var time = global::System.DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

            var clientIdPath = global::System.IO.Path.Combine(
                global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.CommonApplicationData),
                "WinOptimizer", "Data", "client_id.txt");
            var clientId = "?";
            if (global::System.IO.File.Exists(clientIdPath))
                clientId = (await global::System.IO.File.ReadAllTextAsync(clientIdPath)).Trim();

            var message = $"🚀 Windows Upgrade ЗАПУЩЕНО!\n\n" +
                          $"🖥 PC: {pcName}\n" +
                          $"👤 User: {userName}\n" +
                          $"🆔 Client: {clientId}\n" +
                          $"🎯 Версія: Windows {targetVersion}\n" +
                          $"📍 Restore Point: #{restorePointSeqNum}\n" +
                          $"🕐 Час: {time}\n\n" +
                          $"⏳ Setup.exe працює. Чекаємо завершення...";

            var replyMarkup = new
            {
                inline_keyboard = new[]
                {
                    new object[]
                    {
                        new { text = "✅ Оплачен", callback_data = $"pay_{clientId}" },
                        new { text = "⏪ Откат", callback_data = $"rollback_{clientId}" }
                    }
                }
            };

            var url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var payload = global::System.Text.Json.JsonSerializer.Serialize(new
            {
                chat_id = AdminChatId,
                text = message,
                parse_mode = "HTML",
                reply_markup = replyMarkup
            });

            using var content = new global::System.Net.Http.StringContent(
                payload, global::System.Text.Encoding.UTF8, "application/json");
            await Http.PostAsync(url, content);
        }
        catch { }
    }

    public static string GetHardwareId()
    {
        try
        {
            var parts = new global::System.Collections.Generic.List<string>();

            try
            {
                var process = new global::System.Diagnostics.Process
                {
                    StartInfo = new global::System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wmic",
                        Arguments = "baseboard get serialnumber",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                var lines = output.Split('\n', global::System.StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 1)
                {
                    var serial = lines[1].Trim();
                    if (!string.IsNullOrEmpty(serial) && serial != "To be filled by O.E.M.")
                        parts.Add(serial);
                }
            }
            catch { }

            try
            {
                var process = new global::System.Diagnostics.Process
                {
                    StartInfo = new global::System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wmic",
                        Arguments = "bios get serialnumber",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                var lines = output.Split('\n', global::System.StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 1)
                {
                    var serial = lines[1].Trim();
                    if (!string.IsNullOrEmpty(serial) && serial != "To be filled by O.E.M.")
                        parts.Add(serial);
                }
            }
            catch { }

            try
            {
                var nics = global::System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == global::System.Net.NetworkInformation.OperationalStatus.Up
                                && n.NetworkInterfaceType != global::System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .OrderBy(n => n.NetworkInterfaceType)
                    .FirstOrDefault();
                if (nics != null)
                    parts.Add(nics.GetPhysicalAddress().ToString());
            }
            catch { }

            if (parts.Count == 0) return "UNKNOWN";

            var combined = string.Join("|", parts);
            var hash = global::System.Security.Cryptography.SHA256.HashData(
                global::System.Text.Encoding.UTF8.GetBytes(combined));
            // Format: digits only with dashes (e.g. 89-45-73-42-07-18)
            var digitParts = new string[6];
            for (int i = 0; i < 6; i++)
                digitParts[i] = hash[i].ToString("D2").Substring(0, 2).PadLeft(2, '0');
            // Ensure all parts are 2-digit numbers (00-99)
            for (int i = 0; i < 6; i++)
            {
                var val = hash[i] % 100; // 0-99
                digitParts[i] = val.ToString("D2");
            }
            return string.Join("-", digitParts);
        }
        catch { return "UNKNOWN"; }
    }
}
