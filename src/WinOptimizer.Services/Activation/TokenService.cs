using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinOptimizer.Services.Activation;

public static class TokenService
{
    private const string VpsApiUrl = "http://91.236.195.98/api";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex NewFormatRegex = new(@"^\d{2}(-\d{2}){5}$");
    private const string KeyPassphrase = "WinFlow_2026_SecretKey_v1_Production";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(72);

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Reason { get; set; } = "";
        public static ValidationResult Valid() => new() { IsValid = true };
        public static ValidationResult Invalid(string reason) => new() { IsValid = false, Reason = reason };
    }

    // Мастер-код — завжди валідний, не потребує VPS
    private const string MasterToken = "11-22-33-44-55-66";

    public static async Task<ValidationResult> ValidateWithReasonAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ValidationResult.Invalid("empty");
        token = token.Trim();
        if (string.Equals(token, MasterToken, StringComparison.Ordinal))
            return ValidationResult.Valid();
        if (token.StartsWith("WF-", StringComparison.OrdinalIgnoreCase))
            return ValidateOldFormat(token) ? ValidationResult.Valid() : ValidationResult.Invalid("expired");
        if (!NewFormatRegex.IsMatch(token))
            return ValidationResult.Invalid("bad format");
        return await ValidateViaApiWithReasonAsync(token);
    }

    public static async Task<bool> ValidateAsync(string token)
    {
        var result = await ValidateWithReasonAsync(token);
        return result.IsValid;
    }

    public static async Task MarkTokenUsedAsync(string token, string clientId)
    {
        try
        {
            var clean = token.Trim();
            if (!NewFormatRegex.IsMatch(clean)) return;
            var payload = JsonSerializer.Serialize(new { token = clean, client_id = clientId });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await Http.PostAsync($"{VpsApiUrl}/tokens/use", content);
        }
        catch { }
    }

    public static bool Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        if (token.StartsWith("WF-", StringComparison.OrdinalIgnoreCase))
            return ValidateOldFormat(token);
        return false;
    }

    public static TimeSpan GetTokenLifetime() => TokenLifetime;

    private static async Task<ValidationResult> ValidateViaApiWithReasonAsync(string token)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { token });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync($"{VpsApiUrl}/tokens/validate", content);
            if (!response.IsSuccessStatusCode)
                return ValidationResult.Invalid("server error");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                if (root.TryGetProperty("valid", out var valid) && valid.GetBoolean())
                    return ValidationResult.Valid();
                var reason = "not found";
                if (root.TryGetProperty("reason", out var reasonProp))
                    reason = reasonProp.GetString() ?? "unknown";
                return ValidationResult.Invalid(reason);
            }
            return ValidationResult.Invalid("server error");
        }
        // Якщо VPS недоступний — пропускаємо (офлайн-режим)
        catch (TaskCanceledException) { return ValidationResult.Valid(); }
        catch (HttpRequestException) { return ValidationResult.Valid(); }
        catch { return ValidationResult.Valid(); }
    }

    private static bool ValidateOldFormat(string token)
    {
        try
        {
            if (!token.StartsWith("WF-")) return false;
            var combined = FromBase64Url(token[3..]);
            if (combined.Length < 17) return false;
            var key = DeriveKey();
            var iv = new byte[16];
            Buffer.BlockCopy(combined, 0, iv, 0, 16);
            var encrypted = new byte[combined.Length - 16];
            Buffer.BlockCopy(combined, 16, encrypted, 0, encrypted.Length);
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            var payloadStr = Encoding.UTF8.GetString(decrypted);
            var parts = payloadStr.Split(':');
            if (parts.Length != 2) return false;
            if (!long.TryParse(parts[0], out long timestamp)) return false;
            var tokenTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var age = DateTimeOffset.UtcNow - tokenTime;
            return age >= TimeSpan.Zero && age <= TokenLifetime;
        }
        catch { return false; }
    }

    public static string Generate()
    {
        var key = DeriveKey();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var payload = $"{timestamp}:{nonce}";
        using var aes = Aes.Create();
        aes.Key = key; aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(payloadBytes, 0, payloadBytes.Length);
        var combined = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, combined, aes.IV.Length, encrypted.Length);
        return "WF-" + ToBase64Url(combined);
    }

    private static byte[] DeriveKey() => SHA256.HashData(Encoding.UTF8.GetBytes(KeyPassphrase));
    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static byte[] FromBase64Url(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4) { case 2: base64 += "=="; break; case 3: base64 += "="; break; }
        return Convert.FromBase64String(base64);
    }
}
