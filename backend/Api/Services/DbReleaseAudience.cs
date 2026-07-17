using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Ofbirds.ReleaseNotifications;

namespace Api.Services;

/// <summary>
/// Host side of the release-notification protocol (<see cref="IReleaseAudience"/>) over
/// <see cref="AppDbContext"/>. Recipients are the active, opted-in users; the library's own
/// bookkeeping (last-announced release line + notes fingerprint) lives in the SystemSettings
/// KV table, keyed by the library's <see cref="StateKeys"/>. Scoped — the notifier resolves it
/// per-scope on startup, so a per-call DbContext is correct.
/// </summary>
public sealed class DbReleaseAudience(AppDbContext db) : IReleaseAudience
{
    public async Task<IReadOnlyList<ReleaseRecipient>> GetRecipientsAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.NotifyReleases)
            .Select(u => new ReleaseRecipient(u.Email, u.UnsubscribeToken.ToString()))
            .ToListAsync(ct);

    public async Task<string?> GetStateAsync(string key, CancellationToken ct) =>
        (await db.SystemSettings.FindAsync([key], ct))?.Value;

    public async Task SetStateAsync(string key, string value, CancellationToken ct)
    {
        var row = await db.SystemSettings.FindAsync([key], ct);
        if (row is null)
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        else
            row.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
