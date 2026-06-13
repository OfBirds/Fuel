using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

/// <summary>Tests for ReleaseNotifier — BuildEmail and ExecuteAsync with fake email + InMemory DB.</summary>
public class ReleaseNotifierTests
{
    // --- BuildEmail (internal static) --------------------------------------------

    [Fact]
    public void BuildEmail_BulletLines_WrappedInSingleUl()
    {
        var notes = "- First item\n- Second item\n";
        var (_, html, text) = ReleaseNotifier.BuildEmail("1.0", "https://example.com", "https://unsub", notes);

        Assert.Contains("<ul>", html);
        Assert.Contains("</ul>", html);
        Assert.Contains("<li>First item</li>", html);
        Assert.Contains("<li>Second item</li>", html);
        Assert.Contains("• First item", text);
        Assert.Contains("• Second item", text);
    }

    [Fact]
    public void BuildEmail_BlankLine_ParagraphBreak()
    {
        var notes = "Line one\n\nLine two\n";
        var (_, html, _) = ReleaseNotifier.BuildEmail("1.0", "https://x.com", "https://u", notes);

        Assert.Contains("<p>Line one</p>", html);
        Assert.Contains("<p>Line two</p>", html);
    }

    [Fact]
    public void BuildEmail_HtmlEncodesSpecialChars()
    {
        var notes = "<script>alert('xss')</script>";
        var (_, html, _) = ReleaseNotifier.BuildEmail("1.0", "https://x.com", "https://u", notes);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildEmail_EmptyOrWhitespaceNotes_NoNotesBlock()
    {
        var (_, html, _) = ReleaseNotifier.BuildEmail("1.0", "https://x.com", "https://u", null);

        Assert.DoesNotContain("<ul>", html);
    }

    [Fact]
    public void BuildEmail_PlainText_UsesBulletCharacter()
    {
        var notes = "- Alpha\n- Beta\n";
        var (_, _, text) = ReleaseNotifier.BuildEmail("1.0", "https://x.com", "https://u", notes);

        Assert.Contains("• Alpha", text);
        Assert.Contains("• Beta", text);
    }

    [Fact]
    public void BuildEmail_IncludesUnsubscribeLink()
    {
        var (_, html, text) = ReleaseNotifier.BuildEmail("1.0", "https://x.com", "https://unsub/me", null);

        Assert.Contains("https://unsub/me", html);
        Assert.Contains("https://unsub/me", text);
    }

    // --- ExecuteAsync integration (fake email + InMemory DB) ---------------------

    private class FakeEmailSender(string? throwFor = null) : IEmailSender
    {
        public readonly List<(string to, string subject, string html, string text)> Sent = [];

        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody,
            CancellationToken ct = default)
        {
            if (throwFor == toEmail)
                throw new InvalidOperationException($"Simulated failure for {toEmail}");
            Sent.Add((toEmail, subject, htmlBody, textBody));
            return Task.CompletedTask;
        }
    }

    // ExecuteAsync is protected on BackgroundService; expose it so tests can await the
    // run to completion deterministically (StartAsync returns before it finishes, and
    // StopAsync would cancel the very token ExecuteAsync runs on).
    private class TestableNotifier(
        IServiceScopeFactory scopeFactory, IEmailSender email,
        IConfiguration config, ILogger<ReleaseNotifier> logger)
        : ReleaseNotifier(scopeFactory, email, config, logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    /// <summary>
    /// Build a ReleaseNotifier with a real InMemory DbContext registered via DI.
    /// Returns the database name for verification queries.
    /// </summary>
    private static (TestableNotifier notifier, string dbName, FakeEmailSender fake) CreateNotifier(
        bool enabled = true, string version = "1.0.0",
        bool preExistingVersion = false,
        string? throwFor = null)
    {
        var dbName = $"notifier_{Guid.NewGuid()}";

        // Seed the InMemory database.
        var seedOpts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seedDb = new AppDbContext(seedOpts))
        {
            seedDb.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = "user@example.com",
                PasswordHash = "hash",
                IsActive = true,
                NotifyReleases = true
            });

            if (preExistingVersion)
                seedDb.SystemSettings.Add(new SystemSetting { Key = "last_notified_version", Value = version });

            seedDb.SaveChanges();
        }

        // Build DI container for the ReleaseNotifier's scope factory.
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var fake = new FakeEmailSender(throwFor);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NOTIFY_ON_DEPLOY"] = enabled ? "true" : "false",
                ["APP_VERSION"] = version,
                ["PUBLIC_BASE_URL"] = "https://fuel.example.com"
            })
            .Build();

        var notifier = new TestableNotifier(
            scopeFactory,
            fake,
            config,
            NullLogger<ReleaseNotifier>.Instance);

        return (notifier, dbName, fake);
    }

    /// <summary>Create a fresh DbContext for the given InMemory database name.</summary>
    private static AppDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

    [Fact]
    public async Task ExecuteAsync_WhenEnabled_SendsAndRecordsVersion()
    {
        var (notifier, dbName, fake) = CreateNotifier(enabled: true, version: "1.2.3");

        await notifier.RunAsync(CancellationToken.None);

        Assert.Single(fake.Sent);
        Assert.Equal("user@example.com", fake.Sent[0].to);

        using var db = NewDb(dbName);
        var setting = await db.SystemSettings.FindAsync("last_notified_version");
        Assert.NotNull(setting);
        Assert.Equal("1.2.3", setting!.Value);
    }

    [Fact]
    public async Task ExecuteAsync_Idempotent_SameVersionOnlySendsOnce()
    {
        var (notifier, _, fake) = CreateNotifier(
            enabled: true, version: "1.0.0", preExistingVersion: true);

        await notifier.RunAsync(CancellationToken.None);

        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_SendsNothing()
    {
        var (notifier, _, fake) = CreateNotifier(enabled: false, version: "1.0.0");

        await notifier.RunAsync(CancellationToken.None);

        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_VersionDev_SendsNothing()
    {
        var (notifier, _, fake) = CreateNotifier(enabled: true, version: "dev");

        await notifier.RunAsync(CancellationToken.None);

        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_MissingVersion_SendsNothing()
    {
        var (notifier, _, fake) = CreateNotifier(enabled: true, version: "");

        await notifier.RunAsync(CancellationToken.None);

        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_BestEffort_OneFailureDoesNotBlockOthers()
    {
        var (notifier, dbName, fake) = CreateNotifier(
            enabled: true, version: "1.0.0", throwFor: "user@example.com");

        // Seed a second user.
        using (var db = NewDb(dbName))
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = "second@example.com",
                PasswordHash = "hash",
                IsActive = true,
                NotifyReleases = true
            });
            await db.SaveChangesAsync();
        }

        await notifier.RunAsync(CancellationToken.None);

        Assert.Single(fake.Sent);
        Assert.Equal("second@example.com", fake.Sent[0].to);
    }

    [Fact]
    public async Task ExecuteAsync_RecipientFilter_OnlyActiveAndNotifying()
    {
        var (notifier, dbName, fake) = CreateNotifier(enabled: true, version: "1.0.0");

        using (var db = NewDb(dbName))
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = "inactive@example.com",
                PasswordHash = "hash",
                IsActive = false,
                NotifyReleases = true
            });
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = "optedout@example.com",
                PasswordHash = "hash",
                IsActive = true,
                NotifyReleases = false
            });
            await db.SaveChangesAsync();
        }

        await notifier.RunAsync(CancellationToken.None);

        Assert.Single(fake.Sent);
        Assert.Equal("user@example.com", fake.Sent[0].to);
    }
}
