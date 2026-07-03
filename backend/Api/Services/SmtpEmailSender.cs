using Api.Config;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Api.Services;

/// <summary>
/// MailKit-based SMTP sender. A fresh connection per send is fine for this
/// app's volume (a few release emails a week).
/// </summary>
public class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    private readonly SmtpOptions _opt = options.Value;

    public async Task SendAsync(string toEmail, string subject, string htmlBody,
        string textBody, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_opt.From));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        using var client = new SmtpClient();
        if (_opt.AcceptAllCerts)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        // 465 = implicit TLS (SBB); 587 = STARTTLS; otherwise let MailKit decide.
        var security = _opt.Port switch
        {
            465 => SecureSocketOptions.SslOnConnect,
            587 => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.Auto,
        };

        await client.ConnectAsync(_opt.Host, _opt.Port, security, ct);
        await client.AuthenticateAsync(_opt.User, _opt.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        logger.LogInformation("Sent email to {Recipient} ({Subject})", toEmail, subject);
    }
}
