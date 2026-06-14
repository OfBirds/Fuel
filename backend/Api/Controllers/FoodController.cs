using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/foods")]
public class FoodController(AppDbContext db, IFoodService foodService) : ControllerBase
{
    /// <summary>List/search foods. Pass ?search= to filter by name.</summary>
    [HttpGet]
    public async Task<ActionResult<List<FoodListItemResponse>>> GetFoods(
        [FromQuery] string? search, CancellationToken ct)
    {
        var query = db.Foods.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(f => f.Name.ToLower().Contains(search.ToLower()));

        var foods = await query
            .OrderBy(f => f.Name)
            .Select(f => new FoodListItemResponse
            {
                Id = f.Id,
                Name = f.Name,
                DefaultUoM = f.DefaultUoM,
                CaloriesPerUnit = f.CaloriesPerUnit,
                IngredientCount = f.Ingredients.Count,
                IsComposite = f.Ingredients.Count > 0
            })
            .ToListAsync(ct);

        return Ok(foods);
    }

    /// <summary>Get a single food with full ingredient details.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FoodResponse>> GetFood(Guid id, CancellationToken ct)
    {
        var food = await db.Foods
            .Include(f => f.Ingredients)
            .ThenInclude(fi => fi.ChildFood)
            .FirstOrDefaultAsync(f => f.Id == id, ct);

        if (food is null)
            return NotFound();

        return Ok(ToResponse(food));
    }

    /// <summary>Create a food, optionally with ingredient links (including inline child definitions).</summary>
    [HttpPost]
    public async Task<ActionResult<FoodResponse>> CreateFood(
        [FromBody] CreateFoodRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (string.IsNullOrWhiteSpace(request.DefaultUoM))
            return BadRequest(new { error = "DefaultUoM is required." });

        var food = new Food
        {
            Name = request.Name,
            DefaultUoM = request.DefaultUoM,
            CaloriesPerUnit = request.CaloriesPerUnit,
            ProteinPerUnit = request.ProteinPerUnit,
            CarbsPerUnit = request.CarbsPerUnit,
            FatPerUnit = request.FatPerUnit,
        };

        db.Foods.Add(food);

        // Validate and create ingredient links
        if (request.Ingredients.Count > 0)
        {
            var errors = await foodService.ValidateIngredientsAsync(food.Id, request.Ingredients, ct);
            if (errors.Count > 0)
                return BadRequest(new { error = string.Join(" ", errors) });
        }

        await db.SaveChangesAsync(ct);

        foreach (var ing in request.Ingredients)
        {
            Guid childId;
            if (ing.ChildFoodId.HasValue)
            {
                childId = ing.ChildFoodId.Value;
            }
            else if (ing.InlineChild is not null)
            {
                var child = new Food
                {
                    Name = ing.InlineChild.Name,
                    DefaultUoM = ing.InlineChild.DefaultUoM,
                    CaloriesPerUnit = ing.InlineChild.CaloriesPerUnit,
                    ProteinPerUnit = ing.InlineChild.ProteinPerUnit,
                    CarbsPerUnit = ing.InlineChild.CarbsPerUnit,
                    FatPerUnit = ing.InlineChild.FatPerUnit,
                };
                db.Foods.Add(child);
                await db.SaveChangesAsync(ct);
                childId = child.Id;
            }
            else
            {
                continue;
            }

            db.FoodIngredients.Add(new FoodIngredient
            {
                ParentFoodId = food.Id,
                ChildFoodId = childId,
                Quantity = ing.Quantity,
                UoM = ing.UoM,
            });
        }

        await db.SaveChangesAsync(ct);

        // Reload with ingredients for response
        var created = await db.Foods
            .Include(f => f.Ingredients)
            .ThenInclude(fi => fi.ChildFood)
            .FirstAsync(f => f.Id == food.Id, ct);

        return CreatedAtAction(nameof(GetFood), new { id = created.Id }, ToResponse(created));
    }

    /// <summary>Update a food, fully replacing its basic fields and ingredients.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<FoodResponse>> UpdateFood(
        Guid id, [FromBody] UpdateFoodRequest request, CancellationToken ct)
    {
        var food = await db.Foods
            .Include(f => f.Ingredients)
            .FirstOrDefaultAsync(f => f.Id == id, ct);

        if (food is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (string.IsNullOrWhiteSpace(request.DefaultUoM))
            return BadRequest(new { error = "DefaultUoM is required." });

        // Validate ingredient links before modifying
        if (request.Ingredients.Count > 0)
        {
            var errors = await foodService.ValidateIngredientsAsync(id, request.Ingredients, ct);
            if (errors.Count > 0)
                return BadRequest(new { error = string.Join(" ", errors) });
        }

        food.Name = request.Name;
        food.DefaultUoM = request.DefaultUoM;
        food.CaloriesPerUnit = request.CaloriesPerUnit;
        food.ProteinPerUnit = request.ProteinPerUnit;
        food.CarbsPerUnit = request.CarbsPerUnit;
        food.FatPerUnit = request.FatPerUnit;
        food.UpdatedAtUtc = DateTime.UtcNow;

        // Replace all ingredients
        db.FoodIngredients.RemoveRange(food.Ingredients);

        foreach (var ing in request.Ingredients)
        {
            Guid childId;
            if (ing.ChildFoodId.HasValue)
            {
                childId = ing.ChildFoodId.Value;
            }
            else if (ing.InlineChild is not null)
            {
                var child = new Food
                {
                    Name = ing.InlineChild.Name,
                    DefaultUoM = ing.InlineChild.DefaultUoM,
                    CaloriesPerUnit = ing.InlineChild.CaloriesPerUnit,
                    ProteinPerUnit = ing.InlineChild.ProteinPerUnit,
                    CarbsPerUnit = ing.InlineChild.CarbsPerUnit,
                    FatPerUnit = ing.InlineChild.FatPerUnit,
                };
                db.Foods.Add(child);
                await db.SaveChangesAsync(ct);
                childId = child.Id;
            }
            else
            {
                continue;
            }

            db.FoodIngredients.Add(new FoodIngredient
            {
                ParentFoodId = food.Id,
                ChildFoodId = childId,
                Quantity = ing.Quantity,
                UoM = ing.UoM,
            });
        }

        await db.SaveChangesAsync(ct);

        // Reload for response
        var updated = await db.Foods
            .Include(f => f.Ingredients)
            .ThenInclude(fi => fi.ChildFood)
            .FirstAsync(f => f.Id == id, ct);

        return Ok(ToResponse(updated));
    }

    /// <summary>Delete a food. Removes ingredient links and sets FoodEntry references to null.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> DeleteFood(Guid id, CancellationToken ct)
    {
        var food = await db.Foods
            .Include(f => f.Ingredients)
            .FirstOrDefaultAsync(f => f.Id == id, ct);

        if (food is null)
            return NotFound();

        // Remove all ingredient links involving this food
        var asChild = await db.FoodIngredients
            .Where(fi => fi.ChildFoodId == id)
            .ToListAsync(ct);
        db.FoodIngredients.RemoveRange(asChild);
        db.FoodIngredients.RemoveRange(food.Ingredients);

        db.Foods.Remove(food);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    private static FoodResponse ToResponse(Food f) => new()
    {
        Id = f.Id,
        Name = f.Name,
        DefaultUoM = f.DefaultUoM,
        CaloriesPerUnit = f.CaloriesPerUnit,
        ProteinPerUnit = f.ProteinPerUnit,
        CarbsPerUnit = f.CarbsPerUnit,
        FatPerUnit = f.FatPerUnit,
        IconRef = f.IconRef,
        CreatedAtUtc = f.CreatedAtUtc,
        UpdatedAtUtc = f.UpdatedAtUtc,
        Ingredients = f.Ingredients.Select(fi => new IngredientResponse
        {
            ChildFoodId = fi.ChildFoodId,
            ChildFoodName = fi.ChildFood.Name,
            Quantity = fi.Quantity,
            UoM = fi.UoM,
        }).ToList(),
        IsComposite = f.Ingredients.Count > 0,
    };
}
