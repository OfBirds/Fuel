using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

/// <summary>Tests for the periodic backup writer.</summary>
public class BackupServiceTests
{
    [Theory]
    [InlineData("daily", 1)]
    [InlineData("weekly", 7)]
    [InlineData("monthly", 30)]
    [InlineData(null, 30)]
    [InlineData("nonsense", 30)]
    public void ParseInterval_MapsKnownValues(string? value, int expectedDays)
    {
        Assert.Equal(TimeSpan.FromDays(expectedDays), BackupService.ParseInterval(value));
    }

    private static (BackupService svc, string dir) Create(bool seedUsers = true, int? keep = null)
    {
        var dbName = $"backup_{Guid.NewGuid()}";
        var dir = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}");

        var seedOpts = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        using (var db = new AppDbContext(seedOpts))
        {
            if (seedUsers)
            {
                db.Users.Add(new User { Email = "a@x.com", PasswordHash = "h" });
                db.Users.Add(new User { Email = "b@x.com", PasswordHash = "h" });
            }
            db.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BACKUP_DIR"] = dir,
            ["BACKUP_KEEP"] = keep?.ToString(),
        }).Build();

        var svc = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), config,
            NullLogger<BackupService>.Instance);
        return (svc, dir);
    }

    [Fact]
    public async Task RunBackup_WritesSnapshot_WithConventionName()
    {
        var (svc, dir) = Create();

        var written = await svc.RunBackupAsync(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        var file = Assert.Single(written);
        var name = Path.GetFileName(file);
        Assert.StartsWith("fuel-backup-", name);
        Assert.True(File.Exists(file));
        var content = await File.ReadAllTextAsync(file);
        Assert.Contains("\"kind\": \"backup\"", content);
        Assert.Contains("\"userCount\": 2", content);
        // Secrets are never written.
        Assert.DoesNotContain("passwordHash", content);
        Assert.DoesNotContain("unsubscribeToken", content);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task RunBackup_NoUsers_WritesNothing()
    {
        var (svc, dir) = Create(seedUsers: false);

        var written = await svc.RunBackupAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Empty(written);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task RunBackup_PrunesToKeepLimit()
    {
        var (svc, dir) = Create(keep: 1);

        await svc.RunBackupAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        await svc.RunBackupAsync(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        var remaining = Directory.GetFiles(dir, "fuel-backup-*.json");
        Assert.Single(remaining);
        Assert.Contains("20260201", remaining[0]); // newest kept

        Directory.Delete(dir, recursive: true);
    }
}
