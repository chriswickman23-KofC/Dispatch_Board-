using Azure.Communication.Sms;

namespace Dispatch.Web.Services.Notifications;

public interface ITextSender
{
    Task SendAsync(string toE164, string message, CancellationToken ct = default);
}

public sealed class AcsSmsSender(SmsClient client, IConfiguration config) : ITextSender
{
    private string From => config["Sms:From"] ?? throw new InvalidOperationException("Sms:From is not configured.");

    public async Task SendAsync(string toE164, string message, CancellationToken ct = default)
    {
        var result = await client.SendAsync(From, toE164, message, cancellationToken: ct);
        if (!result.Value.Successful)
            throw new InvalidOperationException(result.Value.ErrorMessage ?? "The text message was rejected.");
    }
}

/// <summary>Used when no SMS connection string is configured: logs texts instead of sending them.</summary>
public sealed class LogTextSender(ILogger<LogTextSender> log) : ITextSender
{
    public Task SendAsync(string toE164, string message, CancellationToken ct = default)
    {
        log.LogWarning("SMS (not sent) to {To}: {Message}", toE164, message);
        return Task.CompletedTask;
    }
}

public static class PhoneNumbers
{
    /// <summary>US-focused: "(703) 555-0142" becomes "+17035550142". Returns null if it can't be a valid number.</summary>
    public static string? ToE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (raw.TrimStart().StartsWith('+') && digits.Length is >= 11 and <= 15) return "+" + digits;
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits[0] == '1') return "+" + digits;
        return null;
    }
}
