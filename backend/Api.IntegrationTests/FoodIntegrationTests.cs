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
}

/// <summary>Stub — food-service methods are not exercised by these tests.</summary>
file sealed class NoOpFoodService : IFoodService
{
    public Task<bool> WouldCreateCycle(Guid parentFoodId, Guid childFoodId, CancellationToken ct)
        => Task.FromResult(false);

    public Task<List<string>> ValidateIngredientsAsync(Guid parentFoodId, List<IngredientRequest> ingredients, CancellationToken ct)
        => Task.FromResult(new List<string>());
}
