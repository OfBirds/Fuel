using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired();
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.NotifyReleases).HasDefaultValue(true);
            // gen_random_uuid() backfills existing rows with unique tokens (PG15 core).
            entity.Property(e => e.UnsubscribeToken).HasDefaultValueSql("gen_random_uuid()");
            entity.HasIndex(e => e.UnsubscribeToken).IsUnique();
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
        });
    }
}
