using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Periodically writes a JSON snapshot of the (non-secret) user table to disk so a
/// recent backup always exists. Off unless BACKUP_ENABLED=true. This is the generic
/// "scheduled job + env-gated config + on-disk artifact + retention" pattern —
/// extend RunBackupAsync to snapshot your own tables, or to ship to other
/// destinations (NAS, S3).
/// </summary>
/// <remarks>
/// Env: BACKUP_ENABLED (true to run), BACKUP_INTERVAL (daily|weekly|monthly),
/// BACKUP_DIR (output dir, default "backups"), BACKUP_KEEP (files to retain).
/// Secrets (password hash, unsubscribe token) are never written.
/// </remarks>
public class BackupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<BackupService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var enabled = string.Equals(config["BACKUP_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            logger.LogInformation("Backups disabled (BACKUP_ENABLED not true).");
            return;
        }

        var interval = ParseInterval(config["BACKUP_INTERVAL"]);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var written = await RunBackupAsync(DateTime.UtcNow, ct);
                logger.LogInformation("Backup sweep wrote {Count} file(s).", written.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup sweep failed.");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static TimeSpan ParseInterval(string? value) => (value ?? "monthly").ToLowerInvariant() switch
    {
        "daily" => TimeSpan.FromDays(1),
        "weekly" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30), // "monthly" / default
    };

    /// <summary>
    /// Writes one snapshot file (non-secret user fields) into BACKUP_DIR, pruning to
    /// the last BACKUP_KEEP files. Returns the paths written (none when there are no
    /// users yet).
    /// </summary>
    public async Task<List<string>> RunBackupAsync(DateTime nowUtc, CancellationToken ct)
    {
        var dir = config["BACKUP_DIR"];
        if (string.IsNullOrWhiteSpace(dir))
            dir = "backups";
        Directory.CreateDirectory(dir);

        var keep = int.TryParse(config["BACKUP_KEEP"], out var k) && k > 0 ? k : 12;
        var appVersion = config["APP_VERSION"];

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var users = await db.Users
            .Select(u => new { u.Id, u.Email, u.CreatedAt, u.IsActive, u.NotifyReleases })
            .ToListAsync(ct);

        if (users.Count == 0)
            return []; // nothing to back up yet

        var snapshot = new
        {
            kind = "backup",
            schemaVersion = 1,
            exportedAt = nowUtc,
            appVersion,
            userCount = users.Count,
            users,
        };

        var fileName = $"fuel-backup-{nowUtc:yyyyMMdd-HHmmss}.json";
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, Json), ct);

        Prune(dir, keep);
        return [path];
    }

    /// <summary>Keep only the most recent `keep` backup files; delete older ones.</summary>
    private static void Prune(string dir, int keep)
    {
        var files = Directory.GetFiles(dir, "fuel-backup-*.json")
            .OrderByDescending(f => f)
            .ToList();
        foreach (var old in files.Skip(keep))
        {
            try { File.Delete(old); } catch { /* best effort */ }
        }
    }
}
