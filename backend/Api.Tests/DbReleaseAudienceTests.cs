using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Ofbirds.ReleaseNotifications;

namespace Api.Tests;

/// <summary>
/// Tests for <see cref="DbReleaseAudience"/> — the host side of the shared release-notification
/// protocol: recipient filtering (active + opted-in) and the KV bookkeeping store, over an
/// InMemory DB. The notifier/rendering logic itself lives in and is tested by the
/// Ofbirds.ReleaseNotifications package.
/// </summary>
public class DbReleaseAudienceTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task GetRecipients_ReturnsOnlyActiveOptedIn_WithEmailAndToken()
    {
        var name = $"aud_{Guid.NewGuid()}";
        var wantToken = Guid.NewGuid();
        using (var db = NewDb(name))
        {
            db.Users.Add(new User { Email = "yes@example.com", IsActive = true, NotifyReleases = true, UnsubscribeToken = wantToken });
            db.Users.Add(new User { Email = "inactive@example.com", IsActive = false, NotifyReleases = true });
            db.Users.Add(new User { Email = "optedout@example.com", IsActive = true, NotifyReleases = false });
            await db.SaveChangesAsync();
        }

        using var read = NewDb(name);
        var recipients = await new DbReleaseAudience(read).GetRecipientsAsync(CancellationToken.None);

        var r = Assert.Single(recipients);
        Assert.Equal("yes@example.com", r.Email);
        Assert.Equal(wantToken.ToString(), r.UnsubscribeToken);
    }

    [Fact]
    public async Task State_MissingIsNull_ThenSetInserts_ThenSetUpdates()
    {
        using var db = NewDb($"aud_{Guid.NewGuid()}");
        var audience = new DbReleaseAudience(db);

        Assert.Null(await audience.GetStateAsync(StateKeys.LastNotifiedVersion, CancellationToken.None));

        await audience.SetStateAsync(StateKeys.LastNotifiedVersion, "1.12", CancellationToken.None);
        Assert.Equal("1.12", await audience.GetStateAsync(StateKeys.LastNotifiedVersion, CancellationToken.None));

        await audience.SetStateAsync(StateKeys.LastNotifiedVersion, "1.13", CancellationToken.None);
        Assert.Equal("1.13", await audience.GetStateAsync(StateKeys.LastNotifiedVersion, CancellationToken.None));
    }
}
