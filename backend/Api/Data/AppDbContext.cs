using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<Food> Foods { get; set; }
    public DbSet<FoodIngredient> FoodIngredients { get; set; }
    public DbSet<FoodEntry> FoodEntries { get; set; }
    public DbSet<WeightEntry> WeightEntries { get; set; }

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

            entity.Property(e => e.Sex).HasConversion<string>();
            entity.Property(e => e.Constitution).HasConversion<string>();
            entity.Property(e => e.ShowMacros).HasDefaultValue(false);
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        modelBuilder.Entity<Food>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.DefaultUoM).IsRequired();
            entity.Property(e => e.CaloriesPerUnit).IsRequired();
            entity.HasIndex(e => e.Name);
        });

        modelBuilder.Entity<FoodIngredient>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.ParentFood)
                .WithMany(f => f.Ingredients)
                .HasForeignKey(e => e.ParentFoodId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ChildFood)
                .WithMany(f => f.Parents)
                .HasForeignKey(e => e.ChildFoodId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.UoM).IsRequired();
        });

        modelBuilder.Entity<FoodEntry>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Food)
                .WithMany()
                .HasForeignKey(e => e.FoodId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(e => e.FoodName).IsRequired();
            entity.Property(e => e.MealType)
                .HasConversion<string>()
                .IsRequired();
            entity.Property(e => e.UoM).IsRequired();
            entity.Property(e => e.Source)
                .HasConversion<string>()
                .HasDefaultValue(EntrySource.Manual);

            entity.HasIndex(e => new { e.UserId, e.IntakeAtUtc });
        });

        modelBuilder.Entity<WeightEntry>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Weight).IsRequired();
            entity.HasIndex(e => new { e.UserId, e.RecordedAtUtc });
        });
    }
}
