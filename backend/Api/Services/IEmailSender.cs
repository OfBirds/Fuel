namespace Api.Services;

public interface IEmailSender
{
    /// <summary>Send one email. Throws on failure so callers can log per-recipient.</summary>
    Task SendAsync(string toEmail, string subject, string htmlBody, string textBody,
        CancellationToken ct = default);
}
