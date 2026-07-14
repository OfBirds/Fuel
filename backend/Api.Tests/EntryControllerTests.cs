using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

public class EntryControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly EntryController _controller;
    private readonly Guid _userId;
    private readonly Guid _foodId;

    public EntryControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"entry_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        _controller = new EntryController(_db);

        var user = new User { Email = "test@example.com", PasswordHash = "hash" };
        var food = new Food { Name = "Chicken Breast", DefaultUoM = "g", CaloriesPerUnit = 1.65 };
        _db.Users.Add(user);
        _db.Foods.Add(food);
        _db.SaveChanges();

        _userId = user.Id;
        _foodId = food.Id;
    }

    public void Dispose() => _db.Dispose();

    // ── Create ──

    [Fact]
    public async Task CreateEntry_Basic_CreatesAndReturns()
    {
        var request = new CreateEntryRequest
        {
            FoodId = _foodId,
            FoodName = "Chicken Breast",
            MealType = "Lunch",
            Quantity = 100,
            UoM = "g",
            Calories = 165
        };

        var result = await _controller.CreateEntry(_userId, request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<EntryResponse>(created.Value);
        Assert.Equal("Chicken Breast", response.FoodName);
        Assert.Equal("Lunch", response.MealType);
        Assert.Equal(100, response.Quantity);
        Assert.Equal(165, response.Calories);
        Assert.Equal("Manual", response.Source);
    }

    [Fact]
    public async Task CreateEntry_WithAllFields_StoresSnapshot()
    {
        var request = new CreateEntryRequest
        {
            FoodId = _foodId,
            FoodName = "Chicken Breast",
            MealType = "Dinner",
            Quantity = 200,
            UoM = "g",
            Calories = 330,
            Protein = 62,
            Carbs = 0,
            Fat = 7.2,
            IntakeAtUtc = new DateTime(2026, 6, 14, 18, 0, 0, DateTimeKind.Utc)
        };

        var result = await _controller.CreateEntry(_userId, request, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<EntryResponse>(created.Value);

        Assert.Equal(330, response.Calories);
        Assert.Equal(62, response.Protein);
        Assert.Equal(0, response.Carbs);
        Assert.Equal(7.2, response.Fat);
        Assert.Equal("Dinner", response.MealType);
    }

    [Fact]
    public async Task CreateEntry_UnknownUser_ReturnsNotFound()
    {
        var request = new CreateEntryRequest
        {
            FoodName = "Test", MealType = "Lunch", Quantity = 1, UoM = "g", Calories = 10
        };

        var result = await _controller.CreateEntry(Guid.NewGuid(), request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateEntry_InvalidMealType_ReturnsBadRequest()
    {
        var request = new CreateEntryRequest
        {
            FoodName = "Test", MealType = "Brunch", Quantity = 1, UoM = "g", Calories = 10
        };

        var result = await _controller.CreateEntry(_userId, request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateEntry_DefaultIntakeAtUtc_DefaultsToNow()
    {
        var before = DateTime.UtcNow;
        var request = new CreateEntryRequest
        {
            FoodName = "Test", MealType = "Snack", Quantity = 1, UoM = "g", Calories = 10
        };

        var result = await _controller.CreateEntry(_userId, request, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<EntryResponse>(created.Value);

        Assert.True(response.IntakeAtUtc >= before.AddSeconds(-1));
        Assert.True(response.IntakeAtUtc <= DateTime.UtcNow.AddSeconds(1));
    }

    // ── Batch create (AI multi-item save) ──

    [Fact]
    public async Task CreateEntries_ExistingFood_ReferencesWithoutNewCatalogueRow()
    {
        var foodCountBefore = await _db.Foods.CountAsync();
        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = _foodId, FoodName = "Chicken Breast", MealType = "Lunch",
                    Quantity = 200, UoM = "g", Calories = 330, Confidence = 0.9,
                },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Single(entries);
        Assert.Equal(_foodId, entries[0].FoodId);
        Assert.Equal("AiText", entries[0].Source);
        Assert.Equal(0.9, entries[0].AiConfidence);
        Assert.Equal(foodCountBefore, await _db.Foods.CountAsync()); // no new food
    }

    [Fact]
    public async Task CreateEntries_NewFood_CreatesCatalogueFoodWithDerivedPerUnit()
    {
        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = null, FoodName = "Oatmeal", MealType = "Breakfast",
                    Quantity = 50, UoM = "g", Calories = 180, Protein = 6, Confidence = 0.6,
                },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var entries = Assert.IsType<List<EntryResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Single(entries);
        Assert.NotNull(entries[0].FoodId);

        var food = await _db.Foods.FirstAsync(f => f.Name == "Oatmeal");
        Assert.Equal(180.0 / 50, food.CaloriesPerUnit); // per-unit derived from the row
        Assert.Equal(6.0 / 50, food.ProteinPerUnit);
        Assert.Equal(entries[0].FoodId, food.Id);
    }

    [Fact]
    public async Task CreateEntries_MultipleRows_AllPersist()
    {
        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem { FoodId = _foodId, FoodName = "Chicken Breast", MealType = "Dinner", Quantity = 150, UoM = "g", Calories = 248 },
                new BatchEntryItem { FoodId = null, FoodName = "Rice", MealType = "Dinner", Quantity = 100, UoM = "g", Calories = 130 },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var entries = Assert.IsType<List<EntryResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, await _db.FoodEntries.CountAsync());
    }

    [Fact]
    public async Task CreateEntries_Empty_ReturnsBadRequest()
    {
        var result = await _controller.CreateEntries(_userId, new CreateEntriesBatchRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateEntries_InvalidMealType_ReturnsBadRequest()
    {
        var request = new CreateEntriesBatchRequest
        {
            Items = { new BatchEntryItem { FoodName = "X", MealType = "Brunch", Quantity = 1, UoM = "g", Calories = 1 } },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateEntries_UnknownUser_ReturnsNotFound()
    {
        var request = new CreateEntriesBatchRequest
        {
            Items = { new BatchEntryItem { FoodName = "X", MealType = "Lunch", Quantity = 1, UoM = "g", Calories = 1 } },
        };

        var result = await _controller.CreateEntries(Guid.NewGuid(), request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Read ──

    [Fact]
    public async Task GetEntries_ByDate_ReturnsCorrectRange()
    {
        var day = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodId = _foodId, FoodName = "Chicken Breast",
            MealType = MealType.Lunch, Quantity = 100, UoM = "g", Calories = 165,
            IntakeAtUtc = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetEntries(_userId, null, null, "2026-06-14", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task GetEntries_ByDate_ExcludesOtherDays()
    {
        _db.FoodEntries.AddRange(
            new FoodEntry
            {
                UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
                MealType = MealType.Lunch, Quantity = 100, UoM = "g", Calories = 165,
                IntakeAtUtc = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc)
            },
            new FoodEntry
            {
                UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
                MealType = MealType.Dinner, Quantity = 100, UoM = "g", Calories = 165,
                IntakeAtUtc = new DateTime(2026, 6, 15, 18, 0, 0, DateTimeKind.Utc)
            }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetEntries(_userId, null, null, "2026-06-14", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("Lunch", list[0].MealType);
    }

    [Fact]
    public async Task GetEntries_UnknownUser_ReturnsNotFound()
    {
        var result = await _controller.GetEntries(Guid.NewGuid(), null, null, null, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Update ──

    [Fact]
    public async Task UpdateEntry_UpdatesFields()
    {
        var entry = new FoodEntry
        {
            UserId = _userId, FoodId = _foodId, FoodName = "Chicken Breast",
            MealType = MealType.Lunch, Quantity = 100, UoM = "g", Calories = 165
        };
        _db.FoodEntries.Add(entry);
        await _db.SaveChangesAsync();

        var request = new UpdateEntryRequest { Calories = 200, Quantity = 120 };

        var result = await _controller.UpdateEntry(_userId, entry.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<EntryResponse>(ok.Value);
        Assert.Equal(200, response.Calories);
        Assert.Equal(120, response.Quantity);
        // Unchanged fields remain
        Assert.Equal("Chicken Breast", response.FoodName);
    }

    [Fact]
    public async Task UpdateEntry_NotFound_ReturnsNotFound()
    {
        var request = new UpdateEntryRequest { Calories = 100 };
        var result = await _controller.UpdateEntry(_userId, Guid.NewGuid(), request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateEntry_WrongUser_ReturnsNotFound()
    {
        var entry = new FoodEntry
        {
            UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
            MealType = MealType.Lunch, Quantity = 100, UoM = "g", Calories = 165
        };
        _db.FoodEntries.Add(entry);
        await _db.SaveChangesAsync();

        var request = new UpdateEntryRequest { Calories = 200 };
        var result = await _controller.UpdateEntry(Guid.NewGuid(), entry.Id, request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Delete ──

    [Fact]
    public async Task DeleteEntry_RemovesEntry()
    {
        var entry = new FoodEntry
        {
            UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
            MealType = MealType.Lunch, Quantity = 100, UoM = "g", Calories = 165
        };
        _db.FoodEntries.Add(entry);
        await _db.SaveChangesAsync();

        var result = await _controller.DeleteEntry(_userId, entry.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await _db.FoodEntries.FindAsync(entry.Id));
    }

    [Fact]
    public async Task DeleteEntry_NotFound_ReturnsNotFound()
    {
        var result = await _controller.DeleteEntry(_userId, Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Day total ──

    [Fact]
    public async Task DayTotal_SumOfCalories()
    {
        _db.FoodEntries.AddRange(
            new FoodEntry
            {
                UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
                MealType = MealType.Breakfast, Quantity = 100, UoM = "g", Calories = 300,
                IntakeAtUtc = new DateTime(2026, 6, 14, 8, 0, 0, DateTimeKind.Utc)
            },
            new FoodEntry
            {
                UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
                MealType = MealType.Lunch, Quantity = 200, UoM = "g", Calories = 400,
                IntakeAtUtc = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc)
            },
            new FoodEntry
            {
                UserId = _userId, FoodId = _foodId, FoodName = "Chicken",
                MealType = MealType.Dinner, Quantity = 100, UoM = "g", Calories = 250,
                IntakeAtUtc = new DateTime(2026, 6, 14, 18, 0, 0, DateTimeKind.Utc)
            }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetEntries(_userId, null, null, "2026-06-14", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Equal(3, list.Count);
        Assert.Equal(950, list.Sum(e => e.Calories));
    }

    // ── Dedup: batch get-or-create by normalized name ──

    [Fact]
    public async Task CreateEntries_GetOrCreate_ReusesByNormalizedName()
    {
        // Seed a food with NormalizedName set so the get-or-create path can find it
        var food = await _db.Foods.FindAsync(_foodId);
        food!.NormalizedName = "chicken breast";
        await _db.SaveChangesAsync();
        var foodCountBefore = await _db.Foods.CountAsync();

        // Request a batch entry with the same normalized name but no FoodId
        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = null, FoodName = "Chicken Breast", MealType = "Lunch",
                    Quantity = 200, UoM = "g", Calories = 330,
                },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Single(entries);
        Assert.Equal(_foodId, entries[0].FoodId); // referenced the existing food
        Assert.Equal(foodCountBefore, await _db.Foods.CountAsync()); // no duplicate
    }

    [Fact]
    public async Task CreateEntries_GetOrCreate_DoesNotOverwriteNutrition()
    {
        var food = await _db.Foods.FindAsync(_foodId);
        food!.NormalizedName = "chicken breast";
        food.CaloriesPerUnit = 1.65; // original value
        await _db.SaveChangesAsync();

        // Batch entry with different nutrition — the existing food should be reused as-is
        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = null, FoodName = "Chicken Breast", MealType = "Lunch",
                    Quantity = 100, UoM = "g", Calories = 500, Protein = 100,
                },
            },
        };

        await _controller.CreateEntries(_userId, request, CancellationToken.None);

        // Reload the food — its nutrition should NOT have been overwritten
        var reloaded = await _db.Foods.FindAsync(_foodId);
        Assert.Equal(1.65, reloaded!.CaloriesPerUnit);
        Assert.Null(reloaded.ProteinPerUnit); // original was null
    }

    [Fact]
    public async Task CreateEntries_GetOrCreate_MintsNewWithNormalizedName()
    {
        var foodCountBefore = await _db.Foods.CountAsync();

        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = null, FoodName = "Quinoa Bowl", MealType = "Lunch",
                    Quantity = 200, UoM = "g", Calories = 240, Protein = 8,
                },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Single(entries);
        Assert.NotNull(entries[0].FoodId);

        // Verify exactly one new food was minted with NormalizedName set
        var newFood = await _db.Foods.FirstOrDefaultAsync(f => f.Name == "Quinoa Bowl");
        Assert.NotNull(newFood);
        Assert.Equal("quinoa bowl", newFood!.NormalizedName);
        Assert.Equal(foodCountBefore + 1, await _db.Foods.CountAsync());
    }

    [Fact]
    public async Task CreateEntries_GetOrCreate_CaseVariantReuses()
    {
        var food = await _db.Foods.FindAsync(_foodId);
        food!.NormalizedName = "chicken breast";
        await _db.SaveChangesAsync();
        var foodCountBefore = await _db.Foods.CountAsync();

        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = null, FoodName = "CHICKEN BREAST", MealType = "Lunch",
                    Quantity = 100, UoM = "g", Calories = 165,
                },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Equal(_foodId, entries[0].FoodId);
        Assert.Equal(foodCountBefore, await _db.Foods.CountAsync());
    }

    [Fact]
    public async Task CreateEntries_GetOrCreate_ParensVariantReuses()
    {
        var food = await _db.Foods.FindAsync(_foodId);
        food!.NormalizedName = "chicken breast";
        await _db.SaveChangesAsync();
        var foodCountBefore = await _db.Foods.CountAsync();

        var request = new CreateEntriesBatchRequest
        {
            Items =
            {
                new BatchEntryItem
                {
                    FoodId = null, FoodName = "Chicken Breast (grilled)", MealType = "Lunch",
                    Quantity = 100, UoM = "g", Calories = 165,
                },
            },
        };

        var result = await _controller.CreateEntries(_userId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Equal(_foodId, entries[0].FoodId);
        Assert.Equal(foodCountBefore, await _db.Foods.CountAsync());
    }
}
