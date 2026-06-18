using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// On startup, if NOTIFY_ON_DEPLOY is on and this is a version we haven't
/// announced yet, emails every opted-in user that a new version is out. Runs
/// once per process start; idempotent via the last_notified_version setting, so
/// restarts/redeploys of the same version never re-send.
/// </summary>
public class ReleaseNotifier(
    IServiceScopeFactory scopeFactory,
    IEmailSender email,
    IConfiguration config,
    ILogger<ReleaseNotifier> logger) : BackgroundService
{
    private const string LastNotifiedKey = "last_notified_version";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Always check whether release notes are baked into the image, even on
        // staging (where emails are off), so we can verify the Dockerfile COPY
        // worked before pushing to prod.
        var notesPath = Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md");
        var releaseNotes = File.Exists(notesPath) ? await File.ReadAllTextAsync(notesPath, ct) : null;
        if (!string.IsNullOrWhiteSpace(releaseNotes))
            logger.LogInformation("Release notes found on startup ({Length} chars): {Notes}",
                releaseNotes.Length, releaseNotes.Trim());
        else
            logger.LogInformation("No RELEASE_NOTES.md found on startup.");

        var enabled = string.Equals(config["NOTIFY_ON_DEPLOY"], "true",
            StringComparison.OrdinalIgnoreCase);
        var version = config["APP_VERSION"];

        // Off on staging/local, and we never announce an unversioned dev build.
        if (!enabled || string.IsNullOrWhiteSpace(version) || version == "dev")
        {
            logger.LogInformation(
                "Release notifier idle (enabled={Enabled}, version={Version}).", enabled, version);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var setting = await db.SystemSettings.FindAsync([LastNotifiedKey], ct);
        if (setting?.Value == version)
        {
            logger.LogInformation("Version {Version} already announced; skipping.", version);
            return;
        }

        var baseUrl = (config["PUBLIC_BASE_URL"] ?? "").TrimEnd('/');
        var recipients = await db.Users
            .Where(u => u.IsActive && u.NotifyReleases)
            .Select(u => new { u.Email, u.UnsubscribeToken })
            .ToListAsync(ct);

        logger.LogInformation(
            "Release announcement starting for {Version}: {Recipients} opted-in user(s), link {Link}",
            version, recipients.Count, baseUrl);

        var sent = 0;
        var failed = 0;
        foreach (var r in recipients)
        {
            var unsubscribe = $"{baseUrl}/api/unsubscribe?token={r.UnsubscribeToken}";
            var (subject, html, text) = BuildEmail(version, baseUrl, unsubscribe, releaseNotes);
            try
            {
                await email.SendAsync(r.Email, subject, html, text, ct);
                sent++;
                logger.LogInformation(
                    "Release email sent to {Recipient} for {Version}", r.Email, version);
            }
            catch (Exception ex)
            {
                // Best-effort: log and keep going so one bad address can't block the rest.
                failed++;
                logger.LogError(ex,
                    "Release email FAILED for {Recipient} on {Version}", r.Email, version);
            }
        }

        // Record once the batch has been attempted so a restart doesn't re-send.
        if (setting is null)
            db.SystemSettings.Add(new() { Key = LastNotifiedKey, Value = version });
        else
            setting.Value = version;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Release announcement complete for {Version}: {Sent} sent, {Failed} failed of {Total}",
            version, sent, failed, recipients.Count);
    }

    internal static (string subject, string html, string text) BuildEmail(
        string version, string url, string unsubscribe, string? releaseNotes)
    {
        var subject = $"Indigo Swallow — new version {version} is out";

        // Render the release notes as HTML paragraphs and plain-text lines.
        // Simple conversion: blank-line → paragraph break, bullet → <li>, else <p>.
        var notesHtml = "";
        var notesText = "";
        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            var lines = releaseNotes.Trim().Split('\n');
            var htmlLines = new List<string>();
            var textLines = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (htmlLines.Count > 0 && htmlLines[^1] != "") htmlLines.Add("");
                    continue;
                }
                if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
                {
                    var bullet = line.TrimStart()[2..];
                    htmlLines.Add($"<li>{System.Net.WebUtility.HtmlEncode(bullet)}</li>");
                    textLines.Add($"• {bullet}");
                }
                else
                {
                    htmlLines.Add($"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>");
                    textLines.Add(line);
                }
            }
            // Wrap consecutive <li> in a <ul>.
            var grouped = new List<string>();
            var i = 0;
            while (i < htmlLines.Count)
            {
                if (htmlLines[i].StartsWith("<li>"))
                {
                    grouped.Add("<ul>");
                    while (i < htmlLines.Count && htmlLines[i].StartsWith("<li>"))
                        grouped.Add(htmlLines[i++]);
                    grouped.Add("</ul>");
                }
                else
                {
                    grouped.Add(htmlLines[i++]);
                }
            }
            notesHtml = string.Join("\n", grouped.Where(l => l != ""));
            notesText = string.Join("\n", textLines) + "\n\n";
        }

        var html = $"""
            <p>A new version of <strong>Indigo Swallow</strong> ({version}) has been released.</p>
            {(notesHtml.Length > 0 ? notesHtml + "\n" : "")}
            <p><a href="{url}">Open Indigo Swallow</a></p>
            <hr>
            <p style="font-size:12px;color:#888">
              Don't want these emails? <a href="{unsubscribe}">Unsubscribe</a>.
            </p>
            """;
        var text =
            $"A new version of Indigo Swallow ({version}) has been released.\n\n" +
            $"{notesText}" +
            $"{url}\n\n" +
            $"Unsubscribe: {unsubscribe}";
        return (subject, html, text);
    }
}
