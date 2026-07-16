using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

public class FoodDedupServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly FoodDedupService _service;
    private readonly string _backupDir;
    private readonly string _dbName;

    public FoodDedupServiceTests()
    {
        _dbName = $"dedup_{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        _db = new AppDbContext(options);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseInMemoryDatabase($"dedup_{Guid.NewGuid()}"));
        var sp = services.BuildServiceProvider();

        _backupDir = Path.Combine(Path.GetTempPath(), $"dedup_{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BACKUP_DIR"] = _backupDir,
        }).Build();

        _service = new FoodDedupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<FoodDedupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_backupDir))
            Directory.Delete(_backupDir, recursive: true);
    }

    // ── Survivor selection: SortBySurvivorPriority ────────────────────

    [Fact]
    public void SortBySurvivorPriority_CompositePreferred()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var composite = new Food
        {
            Id = Guid.NewGuid(), Name = "Composite", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            CreatedAtUtc = baseTime.AddDays(1),
        };
        composite.Ingredients.Add(new FoodIngredient
        {
            ParentFoodId = composite.Id, ChildFoodId = Guid.NewGuid(),
            Quantity = 1, UoM = "g",
        });
        var plain = new Food
        {
            Id = Guid.NewGuid(), Name = "Plain", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            CreatedAtUtc = baseTime, // older, but not composite
        };

        var sorted = FoodDedupService.SortBySurvivorPriority([plain, composite]);

        Assert.Equal(composite.Id, sorted[0].Id); // composite wins despite being newer
    }

    [Fact]
    public void SortBySurvivorPriority_BarcodePreferred()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var withBarcode = new Food
        {
            Id = Guid.NewGuid(), Name = "Barcoded", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            Barcode = "123456789",
            CreatedAtUtc = baseTime.AddDays(1),
        };
        var withoutBarcode = new Food
        {
            Id = Guid.NewGuid(), Name = "Plain", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            CreatedAtUtc = baseTime, // older, but no barcode
        };

        var sorted = FoodDedupService.SortBySurvivorPriority([withoutBarcode, withBarcode]);

        Assert.Equal(withBarcode.Id, sorted[0].Id); // barcode wins despite being newer
    }

    [Fact]
    public void SortBySurvivorPriority_CompositeBeatsBarcode()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var barcode = new Food
        {
            Id = Guid.NewGuid(), Name = "Barcoded", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            Barcode = "123456789",
            CreatedAtUtc = baseTime,
        };
        var composite = new Food
        {
            Id = Guid.NewGuid(), Name = "Composite", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            CreatedAtUtc = baseTime.AddDays(1),
        };
        composite.Ingredients.Add(new FoodIngredient
        {
            ParentFoodId = composite.Id, ChildFoodId = Guid.NewGuid(),
            Quantity = 1, UoM = "g",
        });

        var sorted = FoodDedupService.SortBySurvivorPriority([barcode, composite]);

        Assert.Equal(composite.Id, sorted[0].Id); // composite > barcode
    }

    [Fact]
    public void SortBySurvivorPriority_OldestWinsWhenEqual()
    {
        var older = new Food
        {
            Id = Guid.NewGuid(), Name = "Older", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Id = Guid.NewGuid(), Name = "Newer", NormalizedName = "test",
            DefaultUoM = "g", CaloriesPerUnit = 1,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var sorted = FoodDedupService.SortBySurvivorPriority([newer, older]);

        Assert.Equal(older.Id, sorted[0].Id);
    }

    [Fact]
    public void SortBySurvivorPriority_IdBreaksTie()
    {
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lowerId = new Food
        {
            Id = new Guid("00000000-0000-0000-0000-000000000001"),
            Name = "A", NormalizedName = "test", DefaultUoM = "g",
            CaloriesPerUnit = 1, CreatedAtUtc = time,
        };
        var higherId = new Food
        {
            Id = new Guid("00000000-0000-0000-0000-000000000002"),
            Name = "B", NormalizedName = "test", DefaultUoM = "g",
            CaloriesPerUnit = 1, CreatedAtUtc = time,
        };

        var sorted = FoodDedupService.SortBySurvivorPriority([higherId, lowerId]);

        Assert.Equal(lowerId.Id, sorted[0].Id); // lower Guid wins tie
    }

    // ── Group discovery: FindDuplicateGroupsAsync ─────────────────────

    [Fact]
    public async Task FindDuplicateGroups_ReturnsEmpty_WhenNoDuplicates()
    {
        _db.Foods.AddRange(
            new Food { Name = "A", NormalizedName = "a", DefaultUoM = "g", CaloriesPerUnit = 1 },
            new Food { Name = "B", NormalizedName = "b", DefaultUoM = "g", CaloriesPerUnit = 1 }
        );
        await _db.SaveChangesAsync();

        var groups = await FoodDedupService.FindDuplicateGroupsAsync(_db, CancellationToken.None);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task FindDuplicateGroups_GroupsByNormalizedName()
    {
        _db.Foods.AddRange(
            new Food { Name = "Chicken", NormalizedName = "chicken", DefaultUoM = "g", CaloriesPerUnit = 1 },
            new Food { Name = "Chicken Breast (grilled)", NormalizedName = "chicken breast", DefaultUoM = "g", CaloriesPerUnit = 1 },
            new Food { Name = "Chicken Breast", NormalizedName = "chicken breast", DefaultUoM = "g", CaloriesPerUnit = 1 }
        );
        await _db.SaveChangesAsync();

        var groups = await FoodDedupService.FindDuplicateGroupsAsync(_db, CancellationToken.None);

        Assert.Single(groups); // only "chicken breast" has duplicates
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public async Task FindDuplicateGroups_IgnoresEmptyNormalizedName()
    {
        _db.Foods.AddRange(
            new Food { Name = "A", NormalizedName = "", DefaultUoM = "g", CaloriesPerUnit = 1 },
            new Food { Name = "B", NormalizedName = "", DefaultUoM = "g", CaloriesPerUnit = 1 }
        );
        await _db.SaveChangesAsync();

        var groups = await FoodDedupService.FindDuplicateGroupsAsync(_db, CancellationToken.None);

        Assert.Empty(groups); // empty NormalizedName is ignored
    }

    // ── Full dedup pipeline: DeduplicateAsync ─────────────────────────

    [Fact]
    public async Task DeduplicateAsync_NoOp_WhenNoDuplicates()
    {
        _db.Foods.AddRange(
            new Food { Name = "A", NormalizedName = "a", DefaultUoM = "g", CaloriesPerUnit = 1 },
            new Food { Name = "B", NormalizedName = "b", DefaultUoM = "g", CaloriesPerUnit = 1 }
        );
        await _db.SaveChangesAsync();
        var countBefore = await _db.Foods.CountAsync();

        // Create a service that uses the same InMemory database.
        var result = await RunDedupAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(countBefore, await _db.Foods.CountAsync());
    }

    [Fact]
    public async Task DeduplicateAsync_MergesTwoSimpleFoods()
    {
        var older = new Food
        {
            Name = "Chicken Breast", NormalizedName = "chicken breast",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken Breast (grilled)", NormalizedName = "chicken breast",
            DefaultUoM = "g", CaloriesPerUnit = 1.8,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.GroupsFound);
        Assert.Equal(1, result.TotalDuplicates);
        Assert.Equal(1, result.FoodsDeleted);
        Assert.Equal(older.Id, result.Details[0].SurvivorId); // older wins

        // Verify: only survivor remains
        var foods = await _db.Foods.ToListAsync();
        Assert.Single(foods);
        Assert.Equal(older.Id, foods[0].Id);
    }

    [Fact]
    public async Task DeduplicateAsync_RepointsFoodEntries()
    {
        var user = new User { Email = "test@x.com", PasswordHash = "h" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var older = new Food
        {
            Name = "Chicken", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken (fried)", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 2.0,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        // Add food entries pointing to the newer (non-survivor) food
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = user.Id, FoodId = newer.Id, FoodName = newer.Name,
            Quantity = 100, UoM = "g", Calories = 200,
            IntakeAtUtc = DateTime.UtcNow,
        });
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = user.Id, FoodId = older.Id, FoodName = older.Name,
            Quantity = 50, UoM = "g", Calories = 82.5,
            IntakeAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.FoodEntriesRepointed);

        // Verify: all entries now point to survivor
        var entries = await _db.FoodEntries.ToListAsync();
        Assert.All(entries, e => Assert.Equal(older.Id, e.FoodId));
    }

    [Fact]
    public async Task DeduplicateAsync_RepointsFoodIngredients_Child()
    {
        var parent = new Food
        {
            Name = "Salad", NormalizedName = "salad",
            DefaultUoM = "g", CaloriesPerUnit = 1,
        };
        var older = new Food
        {
            Name = "Chicken", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken (fried)", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 2.0,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(parent, older, newer);
        await _db.SaveChangesAsync();

        // Ingredient link points to the newer (non-survivor) chicken
        _db.FoodIngredients.Add(new FoodIngredient
        {
            ParentFoodId = parent.Id, ChildFoodId = newer.Id,
            Quantity = 100, UoM = "g",
        });
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.FoodIngredientsRepointed);

        // Verify: ingredient now points to survivor
        var ingredients = await _db.FoodIngredients.ToListAsync();
        Assert.Single(ingredients);
        Assert.Equal(older.Id, ingredients[0].ChildFoodId);
    }

    [Fact]
    public async Task DeduplicateAsync_RepointsFoodIngredients_Parent()
    {
        var child = new Food
        {
            Name = "Oil", NormalizedName = "oil",
            DefaultUoM = "ml", CaloriesPerUnit = 8.84,
        };
        var otherChild = new Food
        {
            Name = "Vinegar", NormalizedName = "vinegar",
            DefaultUoM = "ml", CaloriesPerUnit = 3,
        };
        var older = new Food
        {
            Name = "Dressing", NormalizedName = "dressing",
            DefaultUoM = "ml", CaloriesPerUnit = 5,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Dressing (creamy)", NormalizedName = "dressing",
            DefaultUoM = "ml", CaloriesPerUnit = 6,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(child, otherChild, older, newer);
        await _db.SaveChangesAsync();

        // Both foods are composite (have their own ingredient) so the
        // composite-preferred survivor rule doesn't disambiguate them —
        // CreatedAtUtc tie-break picks the older as survivor. The newer
        // (non-survivor) is the parent of its own link.
        _db.FoodIngredients.AddRange(
            new FoodIngredient { ParentFoodId = older.Id, ChildFoodId = otherChild.Id, Quantity = 5, UoM = "ml" },
            new FoodIngredient { ParentFoodId = newer.Id, ChildFoodId = child.Id, Quantity = 10, UoM = "ml" }
        );
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.FoodIngredientsRepointed);

        var ingredients = await _db.FoodIngredients.ToListAsync();
        Assert.Equal(2, ingredients.Count);
        Assert.All(ingredients, fi => Assert.Equal(older.Id, fi.ParentFoodId));
    }

    // ── Edge case: self-referential after repointing ──────────────────

    [Fact]
    public async Task DeduplicateAsync_DropsSelfReferentialLinks()
    {
        // Food A contains Food B as ingredient. Both A and B share NormalizedName.
        // After merging B→A, the link becomes self-referential → should be dropped.
        var older = new Food
        {
            Id = Guid.NewGuid(), Name = "Sauce Base",
            NormalizedName = "sauce", DefaultUoM = "ml", CaloriesPerUnit = 1,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Id = Guid.NewGuid(), Name = "Sauce (spicy)",
            NormalizedName = "sauce", DefaultUoM = "ml", CaloriesPerUnit = 1.2,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        // Older contains newer as ingredient — after merge, this becomes self-ref
        _db.FoodIngredients.Add(new FoodIngredient
        {
            ParentFoodId = older.Id, ChildFoodId = newer.Id,
            Quantity = 50, UoM = "ml",
        });
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.SelfReferentialLinksDropped);

        // Verify: the self-referential link is gone
        var ingredients = await _db.FoodIngredients.ToListAsync();
        Assert.Empty(ingredients);
    }

    // ── Edge case: duplicate links after repointing ────────────────────

    [Fact]
    public async Task DeduplicateAsync_DedupesDuplicateLinks()
    {
        var child = new Food
        {
            Name = "Oil", NormalizedName = "oil",
            DefaultUoM = "ml", CaloriesPerUnit = 8.84,
        };
        var older = new Food
        {
            Name = "Dressing", NormalizedName = "dressing",
            DefaultUoM = "ml", CaloriesPerUnit = 5,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Dressing (v2)", NormalizedName = "dressing",
            DefaultUoM = "ml", CaloriesPerUnit = 6,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(child, older, newer);
        await _db.SaveChangesAsync();

        // Both dressings contain oil — after merge, both links point to same pair
        _db.FoodIngredients.AddRange(
            new FoodIngredient
            {
                ParentFoodId = older.Id, ChildFoodId = child.Id,
                Quantity = 10, UoM = "ml",
            },
            new FoodIngredient
            {
                ParentFoodId = newer.Id, ChildFoodId = child.Id,
                Quantity = 20, UoM = "ml",
            }
        );
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.DuplicateLinksDropped);

        // Verify: only one link remains
        var ingredients = await _db.FoodIngredients.ToListAsync();
        Assert.Single(ingredients);
        Assert.Equal(older.Id, ingredients[0].ParentFoodId);
        Assert.Equal(child.Id, ingredients[0].ChildFoodId);
    }

    // ── UserFoodPriority merge ─────────────────────────────────────────

    [Fact]
    public async Task DeduplicateAsync_MergesUserPriorities_NoConflict()
    {
        var user = new User { Email = "test@x.com", PasswordHash = "h" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var older = new Food
        {
            Name = "Chicken", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken (grilled)", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.8,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        // User has priority only for the newer (non-survivor) food
        _db.UserFoodPriorities.Add(new UserFoodPriority
        {
            UserId = user.Id, FoodId = newer.Id, Ponder = 50,
        });
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.Equal(1, result.UserFoodPrioritiesMerged);

        // Verify: priority now points to survivor
        var priorities = await _db.UserFoodPriorities.ToListAsync();
        Assert.Single(priorities);
        Assert.Equal(older.Id, priorities[0].FoodId);
        Assert.Equal(50, priorities[0].Ponder);
    }

    [Fact]
    public async Task DeduplicateAsync_MergesUserPriorities_Conflict_KeepsLower()
    {
        var user = new User { Email = "test@x.com", PasswordHash = "h" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var older = new Food
        {
            Name = "Chicken", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken (grilled)", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.8,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        // Same user has priority for BOTH foods — keep lower Ponder
        _db.UserFoodPriorities.AddRange(
            new UserFoodPriority { UserId = user.Id, FoodId = older.Id, Ponder = 80 },
            new UserFoodPriority { UserId = user.Id, FoodId = newer.Id, Ponder = 30 }
        );
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        // Verify: only survivor's priority remains, with the lower Ponder value
        var priorities = await _db.UserFoodPriorities.ToListAsync();
        Assert.Single(priorities);
        Assert.Equal(older.Id, priorities[0].FoodId);
        Assert.Equal(30, priorities[0].Ponder); // 30 < 80, so 30 wins
    }

    [Fact]
    public async Task DeduplicateAsync_MergesUserPriorities_Conflict_KeepsExistingWhenLower()
    {
        var user = new User { Email = "test@x.com", PasswordHash = "h" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var older = new Food
        {
            Name = "Chicken", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken (grilled)", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.8,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        // Survivor already has the lower Ponder — keep it
        _db.UserFoodPriorities.AddRange(
            new UserFoodPriority { UserId = user.Id, FoodId = older.Id, Ponder = 20 },
            new UserFoodPriority { UserId = user.Id, FoodId = newer.Id, Ponder = 50 }
        );
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        var priorities = await _db.UserFoodPriorities.ToListAsync();
        Assert.Single(priorities);
        Assert.Equal(20, priorities[0].Ponder); // 20 < 50, survivor's 20 wins
    }

    // ── Idempotency ────────────────────────────────────────────────────

    [Fact]
    public async Task DeduplicateAsync_IsIdempotent()
    {
        var older = new Food
        {
            Name = "Chicken", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.65,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Food
        {
            Name = "Chicken (grilled)", NormalizedName = "chicken",
            DefaultUoM = "g", CaloriesPerUnit = 1.8,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Foods.AddRange(older, newer);
        await _db.SaveChangesAsync();

        // First run
        var result1 = await RunDedupAsync();
        Assert.Equal(1, result1.GroupsFound);
        Assert.Equal(1, result1.FoodsDeleted);

        // Second run — no duplicates remain
        var result2 = await RunDedupAsync();
        Assert.Equal(0, result2.GroupsFound);
        Assert.Equal(0, result2.FoodsDeleted);
        Assert.Equal(1, await _db.Foods.CountAsync());
    }

    // ── Snapshot ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeduplicateAsync_WritesSnapshot_WhenDupesFound()
    {
        _db.Foods.AddRange(
            new Food { Name = "A", NormalizedName = "a", DefaultUoM = "g", CaloriesPerUnit = 1,
                CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Food { Name = "A (dup)", NormalizedName = "a", DefaultUoM = "g", CaloriesPerUnit = 2,
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
        );
        await _db.SaveChangesAsync();

        var result = await RunDedupAsync();

        Assert.NotNull(result.SnapshotPath);
        Assert.True(File.Exists(result.SnapshotPath));
        var content = await File.ReadAllTextAsync(result.SnapshotPath);
        Assert.Contains("\"kind\": \"dedup-snapshot\"", content);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Create a service wired to the same InMemory database as _db so
    /// changes made by the service are visible in _db.
    /// </summary>
    private async Task<DedupResult> RunDedupAsync()
    {
        // Build a service that reuses the same InMemory database name as _db,
        // so changes made by the service are visible through _db afterward.
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BACKUP_DIR"] = _backupDir,
        }).Build();

        var svc = new FoodDedupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<FoodDedupService>.Instance);

        var result = await svc.DeduplicateAsync(CancellationToken.None);

        // The service mutated the shared InMemory store through its own DbContext
        // instance; _db's change tracker still holds the pre-dedup entities under
        // their original identities, so clear it to force fresh reads afterward.
        _db.ChangeTracker.Clear();

        return result;
    }
}
