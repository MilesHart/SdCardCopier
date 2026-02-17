namespace SDCardImporter;

/// <summary>
/// Sends a short summary to Telegram after each card is processed.
/// Credentials are read from environment (set in .env or system). Always sends when TELEGRAM_CHAT_ID is set.
/// </summary>
public static class TelegramNotifier
{
    private static readonly HttpClient HttpClient = new();

    /// <summary>Override from --telegram-chat-id (overrides TELEGRAM_CHAT_ID from .env).</summary>
    public static string? ChatIdOverride { get; set; }

    /// <summary>Current effective chat ID: override, then TELEGRAM_CHAT_ID from env (e.g. .env).</summary>
    public static string? GetEffectiveChatId()
    {
        if (!string.IsNullOrWhiteSpace(ChatIdOverride)) return ChatIdOverride.Trim();
        return Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")?.Trim();
    }

    /// <summary>Sends a copy-result message (always call after processing a card, even if 0 files copied).</summary>
    public static async Task SendCopyCompleteAsync(DeviceDetectionResult detection, CopyResult result)
    {
        var status = result.Errors.Count > 0 ? "❌ Failed" : (result.FilesCopied > 0 ? "✅ Success" : "✅ Skipped (nothing new to copy)");
        var deviceName = detection.DeviceType.GetDisplayName();
        var summary = result.Errors.Count > 0
            ? $"{result.FilesCopied} copied, {result.FilesSkipped} skipped, {result.GetFormattedSize()} — {result.Errors.Count} error(s)"
            : $"{result.FilesCopied} copied, {result.FilesSkipped} skipped, {result.GetFormattedSize()}";
        var text = $"SD Card copy complete\nDevice: {deviceName}\nStatus: {status}\n{summary}";
        await SendAsync(text).ConfigureAwait(false);
    }

    /// <summary>Sends a skipped-card message (e.g. unknown device, no files, user said no).</summary>
    public static async Task SendSkippedAsync(DeviceDetectionResult detection, string reason)
    {
        var deviceName = detection.DeviceType.GetDisplayName();
        var text = $"SD Card skipped\nDevice: {deviceName}\nReason: {reason}";
        await SendAsync(text).ConfigureAwait(false);
    }

    private static async Task SendAsync(string text)
    {
        Console.WriteLine("  Sending message to Telegram...");
        var chatId = GetEffectiveChatId();
        if (string.IsNullOrWhiteSpace(chatId))
        {
            Console.WriteLine("  Telegram: TELEGRAM_CHAT_ID not set — add to .env or use --telegram-chat-id <id>.");
            return;
        }

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("  Telegram: TELEGRAM_BOT_TOKEN not set (add to .env) — no notification sent.");
            return;
        }
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", chatId),
                new KeyValuePair<string, string>("text", text)
            });
            using var response = await HttpClient.PostAsync(url, form).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("  Telegram notification sent.");
            }
            else
            {
                Console.WriteLine(string.IsNullOrEmpty(body)
                    ? $"  Telegram notification failed: {response.StatusCode}"
                    : $"  Telegram notification failed: {response.StatusCode} — {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Telegram notification error: {ex.Message}");
        }
    }
}
