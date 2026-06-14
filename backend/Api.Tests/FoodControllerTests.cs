using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

public class FoodControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly FoodController _controller;
    private readonly Guid _chickenId;
    private readonly Guid _oilId;

    public FoodControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"food_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        var foodService = new FoodService(_db);
        _controller = new FoodController(_db, foodService);

        // Seed two standalone foods
        var chicken = new Food { Name = "Chicken Breast", DefaultUoM = "g", CaloriesPerUnit = 1.65 };
        var oil = new Food { Name = "Olive Oil", DefaultUoM = "g", CaloriesPerUnit = 8.84 };
        _db.Foods.AddRange(chicken, oil);
        _db.SaveChanges();

        _chickenId = chicken.Id;
        _oilId = oil.Id;
    }

    public void Dispose() => _db.Dispose();

    // ── Create ──

    [Fact]
    public async Task CreateFood_WithoutIngredients_ReturnsCreated()
    {
        var request = new CreateFoodRequest
        {
            Name = "Brown Rice", DefaultUoM = "g", CaloriesPerUnit = 1.3,
            ProteinPerUnit = 0.03, CarbsPerUnit = 0.28, FatPerUnit = 0.01
        };

        var result = await _controller.CreateFood(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<FoodResponse>(created.Value);
        Assert.Equal("Brown Rice", response.Name);
        Assert.Equal("g", response.DefaultUoM);
        Assert.Equal(1.3, response.CaloriesPerUnit);
        Assert.False(response.IsComposite);

        // Verify persisted
        var inDb = await _db.Foods.FindAsync(response.Id);
        Assert.NotNull(inDb);
        Assert.Equal("Brown Rice", inDb!.Name);
    }

    [Fact]
    public async Task CreateFood_WithExistingChild_LinksIngredient()
    {
        var request = new CreateFoodRequest
        {
            Name = "Chicken Salad", DefaultUoM = "g", CaloriesPerUnit = 4.0,
            Ingredients = [new IngredientRequest { ChildFoodId = _chickenId, Quantity = 200, UoM = "g" }]
        };

        var result = await _controller.CreateFood(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<FoodResponse>(created.Value);
        Assert.True(response.IsComposite);
        Assert.Single(response.Ingredients);
        Assert.Equal(_chickenId, response.Ingredients[0].ChildFoodId);
    }

    [Fact]
    public async Task CreateFood_WithInlineChild_CreatesBoth()
    {
        var request = new CreateFoodRequest
        {
            Name = "Custom Mix", DefaultUoM = "g", CaloriesPerUnit = 5.0,
            Ingredients =
            [
                new IngredientRequest
                {
                    InlineChild = new InlineChildRequest
                    {
                        Name = "Custom Oil", DefaultUoM = "g", CaloriesPerUnit = 9.0
                    },
                    Quantity = 10, UoM = "g"
                }
            ]
        };

        var result = await _controller.CreateFood(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<FoodResponse>(created.Value);
        Assert.Single(response.Ingredients);
        Assert.Contains("Custom Oil", response.Ingredients[0].ChildFoodName);

        // Verify the inline child was actually created
        var childInDb = await _db.Foods.FindAsync(response.Ingredients[0].ChildFoodId);
        Assert.NotNull(childInDb);
        Assert.Equal("Custom Oil", childInDb!.Name);
    }

    [Fact]
    public async Task CreateFood_SelfIngredient_ReturnsBadRequest()
    {
        // Create a food, then try to make it contain itself via update
        var food = new Food { Name = "Paradox", DefaultUoM = "g", CaloriesPerUnit = 1 };
        _db.Foods.Add(food);
        await _db.SaveChangesAsync();

        var request = new UpdateFoodRequest
        {
            Name = "Paradox", DefaultUoM = "g", CaloriesPerUnit = 1,
            Ingredients = [new IngredientRequest { ChildFoodId = food.Id, Quantity = 1, UoM = "g" }]
        };

        var result = await _controller.UpdateFood(food.Id, request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateFood_TransitiveCycle_ReturnsBadRequest()
    {
        // A contains B, now try to make B contain A
        var a = new Food { Name = "A", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var b = new Food { Name = "B", DefaultUoM = "g", CaloriesPerUnit = 1 };
        _db.Foods.AddRange(a, b);
        await _db.SaveChangesAsync();

        _db.FoodIngredients.Add(new FoodIngredient { ParentFoodId = a.Id, ChildFoodId = b.Id, Quantity = 1, UoM = "g" });
        await _db.SaveChangesAsync();

        var request = new UpdateFoodRequest
        {
            Name = "B", DefaultUoM = "g", CaloriesPerUnit = 1,
            Ingredients = [new IngredientRequest { ChildFoodId = a.Id, Quantity = 1, UoM = "g" }]
        };

        var result = await _controller.UpdateFood(b.Id, request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateFood_MissingName_ReturnsBadRequest()
    {
        var request = new CreateFoodRequest
        {
            Name = "", DefaultUoM = "g", CaloriesPerUnit = 1
        };

        var result = await _controller.CreateFood(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ── Read ──

    [Fact]
    public async Task GetFoods_ReturnsAll()
    {
        var result = await _controller.GetFoods(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetFoods_WithSearch_Filters()
    {
        var result = await _controller.GetFoods("chicken", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<FoodListItemResponse>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("Chicken Breast", list[0].Name);
    }

    [Fact]
    public async Task GetFood_ReturnsFullDetail()
    {
        // Add an ingredient to chicken
        _db.FoodIngredients.Add(new FoodIngredient
        {
            ParentFoodId = _chickenId, ChildFoodId = _oilId, Quantity = 5, UoM = "g"
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetFood(_chickenId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<FoodResponse>(ok.Value);
        Assert.Equal("Chicken Breast", response.Name);
        Assert.Single(response.Ingredients);
        Assert.Equal(_oilId, response.Ingredients[0].ChildFoodId);
        Assert.True(response.IsComposite);
    }

    [Fact]
    public async Task GetFood_UnknownId_ReturnsNotFound()
    {
        var result = await _controller.GetFood(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Update ──

    [Fact]
    public async Task UpdateFood_ReplacesBasicFields()
    {
        var request = new UpdateFoodRequest
        {
            Name = "Grilled Chicken", DefaultUoM = "piece", CaloriesPerUnit = 250,
            ProteinPerUnit = 50, Ingredients = []
        };

        var result = await _controller.UpdateFood(_chickenId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<FoodResponse>(ok.Value);
        Assert.Equal("Grilled Chicken", response.Name);
        Assert.Equal("piece", response.DefaultUoM);
        Assert.Equal(250, response.CaloriesPerUnit);
    }

    [Fact]
    public async Task UpdateFood_ReplacesIngredients()
    {
        var request = new UpdateFoodRequest
        {
            Name = "Chicken Breast", DefaultUoM = "g", CaloriesPerUnit = 1.65,
            Ingredients = [new IngredientRequest { ChildFoodId = _oilId, Quantity = 10, UoM = "ml" }]
        };

        var result = await _controller.UpdateFood(_chickenId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<FoodResponse>(ok.Value);
        Assert.Single(response.Ingredients);
        Assert.Equal(_oilId, response.Ingredients[0].ChildFoodId);
    }

    [Fact]
    public async Task UpdateFood_UnknownId_ReturnsNotFound()
    {
        var request = new UpdateFoodRequest { Name = "X", DefaultUoM = "g", CaloriesPerUnit = 1 };
        var result = await _controller.UpdateFood(Guid.NewGuid(), request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Delete ──

    [Fact]
    public async Task DeleteFood_RemovesFoodAndLinks_ReturnsNoContent()
    {
        var result = await _controller.DeleteFood(_chickenId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await _db.Foods.FindAsync(_chickenId));
    }

    [Fact]
    public async Task DeleteFood_UnknownId_ReturnsNotFound()
    {
        var result = await _controller.DeleteFood(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }
}
