using Api.Controllers;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.IntegrationTests;

/// <summary>
/// Real-Postgres integration tests for <see cref="FoodController"/> —
/// catches projection/translation bugs InMemory silently accepts.
/// </summary>
[Collection("Postgres")]
public class FoodIntegrationTests(PostgresFixture db)
{
    [Fact]
    public async Task GetFoods_CompositeFood_HasIngredientCount()
    {
        // ── Arrange ────────────────────────────────────────────────
        await db.ResetAsync();
        var ctx = db.CreateContext();
        var oil = new Food { Name = "olive oil", DefaultUoM = "ml", CaloriesPerUnit = 8 };
        ctx.Foods.Add(oil);
        await ctx.SaveChangesAsync();

        var salad = new Food { Name = "salad", DefaultUoM = "g", CaloriesPerUnit = 1.5 };
        salad.Ingredients.Add(new FoodIngredient
        {
            ParentFoodId = salad.Id, ChildFoodId = oil.Id, Quantity = 15, UoM = "ml"
        });
        ctx.Foods.Add(salad);
        await ctx.SaveChangesAsync();

        // ── Act ────────────────────────────────────────────────────
        var controller = new FoodController(ctx, new NoOpFoodService());
        var result = await controller.GetFoods(search: null, userId: null, sort: null, CancellationToken.None);

        // ── Assert ─────────────────────────────────────────────────
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        var saladItem = Assert.Single(list, f => f.Name == "salad");

        Assert.True(saladItem.IsComposite);
        Assert.Equal(1, saladItem.IngredientCount);
    }

    [Fact]
    public async Task GetFoods_NonCompositeFood_IngredientCountZero()
    {
        await db.ResetAsync();
        var ctx = db.CreateContext();
        ctx.Foods.Add(new Food { Name = "apple", DefaultUoM = "pc", CaloriesPerUnit = 95 });
        await ctx.SaveChangesAsync();

        var controller = new FoodController(ctx, new NoOpFoodService());
        var result = await controller.GetFoods(search: null, userId: null, sort: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        var apple = Assert.Single(list, f => f.Name == "apple");

        Assert.False(apple.IsComposite);
        Assert.Equal(0, apple.IngredientCount);
    }

    // ── Sort modes ────────────────────────────────────────────────
    // These exercise the provider-specific SQL (GroupJoin + aggregate + null-coalescing
    // OrderBy) that EF InMemory translates differently from real Npgsql — the exact
    // reason this integration tier exists.  All assert on full-list ordering, so each
    // resets the DB first.

    [Fact]
    public async Task GetFoods_PrioritySort_OrdersByPonderThenName_AbsentRowDefaults100()
    {
        await db.ResetAsync();
        var ctx = db.CreateContext();
        var userId = Guid.NewGuid();
        ctx.Users.Add(new User { Id = userId, Email = "prio@example.com", PasswordHash = "hash" });

        var aardvark = new Food { Name = "aardvark-snack", DefaultUoM = "g", CaloriesPerUnit = 1 }; // no priority → 100
        var beans    = new Food { Name = "beans",          DefaultUoM = "g", CaloriesPerUnit = 1 }; // ponder 1
        var carrot   = new Food { Name = "carrot",         DefaultUoM = "g", CaloriesPerUnit = 1 }; // ponder 50
        ctx.Foods.AddRange(aardvark, beans, carrot);
        ctx.UserFoodPriorities.AddRange(
            new UserFoodPriority { UserId = userId, FoodId = beans.Id, Ponder = 1 },
            new UserFoodPriority { UserId = userId, FoodId = carrot.Id, Ponder = 50 }
        );
        await ctx.SaveChangesAsync();

        var controller = new FoodController(ctx, new NoOpFoodService());
        var result = await controller.GetFoods(search: null, userId: userId, sort: "priority", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        // beans (1) < carrot (50) < aardvark-snack (absent → 100), despite alphabetical order.
        Assert.Equal(new[] { "beans", "carrot", "aardvark-snack" }, list.Select(f => f.Name).ToArray());
        Assert.Equal(1, list[0].Ponder);
        Assert.Null(list[2].Ponder); // absent priority surfaces as null in the projection
    }

    [Fact]
    public async Task GetFoods_MostUsedSort_OrdersByEntryCount_AndPopulatesUsageCount()
    {
        await db.ResetAsync();
        var ctx = db.CreateContext();
        var userId = Guid.NewGuid();
        ctx.Users.Add(new User { Id = userId, Email = "used@example.com", PasswordHash = "hash" });

        var popular = new Food { Name = "popular", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var rare    = new Food { Name = "rare",    DefaultUoM = "g", CaloriesPerUnit = 1 };
        var never   = new Food { Name = "never",   DefaultUoM = "g", CaloriesPerUnit = 1 };
        ctx.Foods.AddRange(popular, rare, never);
        await ctx.SaveChangesAsync();

        var t = new DateTime(2026, 6, 14, 8, 0, 0, DateTimeKind.Utc);
        ctx.FoodEntries.AddRange(
            new FoodEntry { UserId = userId, FoodId = popular.Id, FoodName = "popular", UoM = "g", Calories = 1, Quantity = 1, IntakeAtUtc = t },
            new FoodEntry { UserId = userId, FoodId = popular.Id, FoodName = "popular", UoM = "g", Calories = 1, Quantity = 1, IntakeAtUtc = t.AddHours(1) },
            new FoodEntry { UserId = userId, FoodId = popular.Id, FoodName = "popular", UoM = "g", Calories = 1, Quantity = 1, IntakeAtUtc = t.AddHours(2) },
            new FoodEntry { UserId = userId, FoodId = rare.Id,    FoodName = "rare",    UoM = "g", Calories = 1, Quantity = 1, IntakeAtUtc = t }
        );
        await ctx.SaveChangesAsync();

        var controller = new FoodController(ctx, new NoOpFoodService());
        var result = await controller.GetFoods(search: null, userId: userId, sort: "most-used", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        Assert.Equal(new[] { "popular", "rare", "never" }, list.Select(f => f.Name).ToArray());
        Assert.Equal(3, list[0].UsageCount);
        Assert.Equal(1, list[1].UsageCount);
        Assert.Null(list[2].UsageCount); // zero usage suppressed to null
    }

    [Fact]
    public async Task GetFoods_RecentSort_OrdersByLastIntake_UnusedLast()
    {
        await db.ResetAsync();
        var ctx = db.CreateContext();
        var userId = Guid.NewGuid();
        ctx.Users.Add(new User { Id = userId, Email = "recent@example.com", PasswordHash = "hash" });

        var newest = new Food { Name = "newest", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var older  = new Food { Name = "older",  DefaultUoM = "g", CaloriesPerUnit = 1 };
        var unused = new Food { Name = "unused", DefaultUoM = "g", CaloriesPerUnit = 1 };
        ctx.Foods.AddRange(newest, older, unused);
        await ctx.SaveChangesAsync();

        var t = new DateTime(2026, 6, 14, 8, 0, 0, DateTimeKind.Utc);
        ctx.FoodEntries.AddRange(
            new FoodEntry { UserId = userId, FoodId = older.Id,  FoodName = "older",  UoM = "g", Calories = 1, Quantity = 1, IntakeAtUtc = t },
            new FoodEntry { UserId = userId, FoodId = newest.Id, FoodName = "newest", UoM = "g", Calories = 1, Quantity = 1, IntakeAtUtc = t.AddHours(5) }
        );
        await ctx.SaveChangesAsync();

        var controller = new FoodController(ctx, new NoOpFoodService());
        // The `recent` branch orders by `MaxIntake ?? DateTime.MinValue` — feeding
        // DateTime.MinValue (Kind=Unspecified) into a timestamptz comparison is exactly
        // the kind of thing real Npgsql can reject and InMemory cannot catch.
        var result = await controller.GetFoods(search: null, userId: userId, sort: "recent", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        Assert.Equal(new[] { "newest", "older", "unused" }, list.Select(f => f.Name).ToArray());
        Assert.Null(list[2].LastUsedAtUtc); // never-used food has no last-intake
    }

    // ── Cycle detection (real FoodService against Postgres) ───────

    [Fact]
    public async Task WouldCreateCycle_ChainClosingBackToParent_ReturnsTrue()
    {
        await db.ResetAsync();
        var ctx = db.CreateContext();

        // a → b → c (each food lists the next as an ingredient).
        var a = new Food { Name = "a", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var b = new Food { Name = "b", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var c = new Food { Name = "c", DefaultUoM = "g", CaloriesPerUnit = 1 };
        ctx.Foods.AddRange(a, b, c);
        await ctx.SaveChangesAsync();
        ctx.FoodIngredients.AddRange(
            new FoodIngredient { ParentFoodId = a.Id, ChildFoodId = b.Id, Quantity = 1, UoM = "g" },
            new FoodIngredient { ParentFoodId = b.Id, ChildFoodId = c.Id, Quantity = 1, UoM = "g" }
        );
        await ctx.SaveChangesAsync();

        var svc = new FoodService(ctx);
        // Adding `a` as an ingredient of `c` would close the loop c → a → b → c.
        Assert.True(await svc.WouldCreateCycle(parentFoodId: c.Id, childFoodId: a.Id, CancellationToken.None));
    }

    [Fact]
    public async Task WouldCreateCycle_DisjointChains_ReturnsFalse()
    {
        await db.ResetAsync();
        var ctx = db.CreateContext();

        var a = new Food { Name = "a", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var b = new Food { Name = "b", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var loose = new Food { Name = "loose", DefaultUoM = "g", CaloriesPerUnit = 1 };
        ctx.Foods.AddRange(a, b, loose);
        await ctx.SaveChangesAsync();
        ctx.FoodIngredients.Add(new FoodIngredient { ParentFoodId = a.Id, ChildFoodId = b.Id, Quantity = 1, UoM = "g" });
        await ctx.SaveChangesAsync();

        var svc = new FoodService(ctx);
        // `loose` is unrelated, so adding it under `b` cannot create a cycle.
        Assert.False(await svc.WouldCreateCycle(parentFoodId: b.Id, childFoodId: loose.Id, CancellationToken.None));
    }
}

/// <summary>Stub — food-service methods are not exercised by the controller-list tests.</summary>
file sealed class NoOpFoodService : IFoodService
{
    public Task<bool> WouldCreateCycle(Guid parentFoodId, Guid childFoodId, CancellationToken ct)
        => Task.FromResult(false);

    public Task<List<string>> ValidateIngredientsAsync(Guid parentFoodId, List<IngredientRequest> ingredients, CancellationToken ct)
        => Task.FromResult(new List<string>());
}
