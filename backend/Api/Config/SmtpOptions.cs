namespace Api.Config;

/// <summary>
/// SMTP settings for outbound mail. Populated from the flat SMTP_* env keys in
/// Program.cs (same style as the DB_* keys) so the password stays in the env
/// file, never in source.
/// </summary>
public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 465;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";

    /// <summary>
    /// Accept any server certificate. Needed for ISP mail servers (e.g. SBB)
    /// whose certs don't chain cleanly. Only set for hosts you trust.
    /// </summary>
    public bool AcceptAllCerts { get; set; } = false;

    /// <summary>True only when a host is configured; lets callers no-op cleanly.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
