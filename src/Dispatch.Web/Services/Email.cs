using System.Text.Encodings.Web;
using Azure;
using Azure.Communication.Email;
using Dispatch.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Dispatch.Web.Services;

public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string html, string text, CancellationToken ct = default);
}

public sealed class AcsEmailSender(EmailClient client, IConfiguration config) : IAppEmailSender
{
    private string From => config["Email:From"]
        ?? throw new InvalidOperationException("Email:From is not configured.");

    public Task SendAsync(string to, string subject, string html, string text, CancellationToken ct = default) =>
        client.SendAsync(WaitUntil.Started, From, to, subject, html, text, ct);
}

/// <summary>Development: writes emails (including invite links) to the console instead of sending them.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> log) : IAppEmailSender
{
    public Task SendAsync(string to, string subject, string html, string text, CancellationToken ct = default)
    {
        log.LogWarning("EMAIL (not sent)\nTo: {To}\nSubject: {Subject}\n\n{Text}", to, subject, text);
        return Task.CompletedTask;
    }
}

/// <summary>Lets the template's "Forgot password" pages send real emails too.</summary>
public sealed class IdentityEmailSender(IAppEmailSender email) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string address, string confirmationLink) =>
        email.SendAsync(address, "Confirm your email",
            $"<p>Please <a href=\"{confirmationLink}\">confirm your account</a>.</p>",
            "Please confirm your account using the link in the HTML version of this email.");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string address, string resetLink) =>
        email.SendAsync(address, "Reset your Dispatch password",
            $"<p><a href=\"{resetLink}\">Reset your password</a>. If you didn't ask for this, you can ignore this email.</p>",
            "Use the link in the HTML version of this email to reset your password.");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string address, string resetCode) =>
        email.SendAsync(address, "Your password reset code",
            $"<p>Your reset code is <strong>{HtmlEncoder.Default.Encode(resetCode)}</strong>.</p>",
            $"Your reset code is {resetCode}.");
}
