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
    /// <summary>List/search foods with optional per-user sort and priority data.</summary>
    [HttpGet]
    public async Task<ActionResult<List<FoodListItemResponse>>> GetFoods(
        [FromQuery] string? search = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? sort = null,
        CancellationToken ct = default)
    {
        var query = db.Foods.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(f => f.Name.ToLower().Contains(search.ToLower()));

        // Determine sort mode: priority requires userId; without userId, fall back to alphabetical.
        var sortMode = !string.IsNullOrWhiteSpace(sort) ? sort : (userId.HasValue ? "priority" : "alphabetical");

        List<FoodListItemResponse> foods;

        switch (sortMode)
        {
            case "priority" when userId.HasValue:
                foods = await query
                    .GroupJoin(db.UserFoodPriorities.Where(p => p.UserId == userId.Value),
                        f => f.Id, p => p.FoodId,
                        (f, ps) => new { Food = f, Ponder = ps.Select(p => (int?)p.Ponder).FirstOrDefault() })
                    .OrderBy(x => x.Ponder ?? 100)
                    .ThenBy(x => x.Food.Name)
                    .Select(x => new FoodListItemResponse
                    {
                        Id = x.Food.Id,
                        Name = x.Food.Name,
                        DefaultUoM = x.Food.DefaultUoM,
                        CaloriesPerUnit = x.Food.CaloriesPerUnit,
                        IngredientCount = x.Food.Ingredients.Count,
                        IsComposite = x.Food.Ingredients.Count > 0,
                        Ponder = x.Ponder,
                    })
                    .ToListAsync(ct);
                break;

            case "most-used" when userId.HasValue:
                foods = await query
                    .GroupJoin(db.FoodEntries.Where(e => e.UserId == userId.Value && e.FoodId != null),
                        f => f.Id, e => e.FoodId!.Value,
                        (f, entries) => new { Food = f, Count = entries.Count(), MaxIntake = entries.Max(e => (DateTime?)e.IntakeAtUtc) })
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Food.Name)
                    .Select(x => new FoodListItemResponse
                    {
                        Id = x.Food.Id,
                        Name = x.Food.Name,
                        DefaultUoM = x.Food.DefaultUoM,
                        CaloriesPerUnit = x.Food.CaloriesPerUnit,
                        IngredientCount = x.Food.Ingredients.Count,
                        IsComposite = x.Food.Ingredients.Count > 0,
                        UsageCount = x.Count > 0 ? x.Count : null,
                        LastUsedAtUtc = x.MaxIntake,
                    })
                    .ToListAsync(ct);
                break;

            case "recent" when userId.HasValue:
                foods = await query
                    .GroupJoin(db.FoodEntries.Where(e => e.UserId == userId.Value && e.FoodId != null),
                        f => f.Id, e => e.FoodId!.Value,
                        (f, entries) => new { Food = f, MaxIntake = entries.Max(e => (DateTime?)e.IntakeAtUtc) })
                    .OrderByDescending(x => x.MaxIntake ?? DateTime.MinValue)
                    .ThenBy(x => x.Food.Name)
                    .Select(x => new FoodListItemResponse
                    {
                        Id = x.Food.Id,
                        Name = x.Food.Name,
                        DefaultUoM = x.Food.DefaultUoM,
                        CaloriesPerUnit = x.Food.CaloriesPerUnit,
                        IngredientCount = x.Food.Ingredients.Count,
                        IsComposite = x.Food.Ingredients.Count > 0,
                        LastUsedAtUtc = x.MaxIntake,
                    })
                    .ToListAsync(ct);
                break;

            default:
                // Alphabetical (or fallback when userId missing for per-user sorts)
                foods = await query
                    .OrderBy(f => f.Name)
                    .Select(f => new FoodListItemResponse
                    {
                        Id = f.Id,
                        Name = f.Name,
                        DefaultUoM = f.DefaultUoM,
                        CaloriesPerUnit = f.CaloriesPerUnit,
                        IngredientCount = f.Ingredients.Count,
                        IsComposite = f.Ingredients.Count > 0,
                    })
                    .ToListAsync(ct);
                break;
        }

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

        var normalized = FoodNameNormalizer.Normalize(request.Name);

        // Reject explicit "add food" on a name that already exists in the catalogue.
        var existing = await db.Foods
            .Where(f => f.NormalizedName == normalized)
            .Select(f => new { f.Id })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return Conflict(new { error = "A food with this name already exists.", existingFoodId = existing.Id });

        var food = new Food
        {
            Name = request.Name,
            NormalizedName = normalized,
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
                // Silent get-or-create: inline children are not user-named-checked.
                childId = await GetOrCreateChildFoodAsync(ing.InlineChild, ct);
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

        // If the name changed, check for collision with an EXISTING food (not self).
        var newNormalized = FoodNameNormalizer.Normalize(request.Name);
        if (!string.Equals(food.NormalizedName, newNormalized, StringComparison.Ordinal))
        {
            var collision = await db.Foods
                .Where(f => f.NormalizedName == newNormalized && f.Id != id)
                .Select(f => new { f.Id })
                .FirstOrDefaultAsync(ct);
            if (collision is not null)
                return Conflict(new { error = "A food with this name already exists.", existingFoodId = collision.Id });
        }

        // Validate ingredient links before modifying
        if (request.Ingredients.Count > 0)
        {
            var errors = await foodService.ValidateIngredientsAsync(id, request.Ingredients, ct);
            if (errors.Count > 0)
                return BadRequest(new { error = string.Join(" ", errors) });
        }

        food.Name = request.Name;
        food.NormalizedName = newNormalized;
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
                // Silent get-or-create: inline children are not user-named-checked.
                childId = await GetOrCreateChildFoodAsync(ing.InlineChild, ct);
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

    /// <summary>Set per-user priority (ponder) for a food. Upsert — lower = higher priority.</summary>
    [HttpPut("{foodId:guid}/priority")]
    public async Task<ActionResult> SetPriority(
        Guid foodId, [FromQuery] Guid userId, [FromBody] SetPriorityRequest request, CancellationToken ct)
    {
        if (request.Ponder < 0)
            return BadRequest(new { error = "Ponder must be >= 0." });

        var foodExists = await db.Foods.AnyAsync(f => f.Id == foodId, ct);
        if (!foodExists)
            return NotFound();

        var existing = await db.UserFoodPriorities
            .FirstOrDefaultAsync(p => p.UserId == userId && p.FoodId == foodId, ct);

        if (existing is not null)
        {
            if (request.Ponder == 100)
            {
                db.UserFoodPriorities.Remove(existing);
            }
            else
            {
                existing.Ponder = request.Ponder;
            }
        }
        else
        {
            if (request.Ponder != 100)
            {
                db.UserFoodPriorities.Add(new UserFoodPriority
                {
                    UserId = userId,
                    FoodId = foodId,
                    Ponder = request.Ponder,
                });
            }
            // Ponder == 100 and no row → no-op (already default)
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Silent get-or-create for an inline child ingredient. The user didn't
    /// explicitly name-check this — look up by normalized name, reference on hit,
    /// mint on miss. Never overwrites an existing food's nutrition.</summary>
    private async Task<Guid> GetOrCreateChildFoodAsync(InlineChildRequest child, CancellationToken ct)
    {
        var normalized = FoodNameNormalizer.Normalize(child.Name);
        var existing = await db.Foods
            .Where(f => f.NormalizedName == normalized)
            .Select(f => new { f.Id })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return existing.Id;

        var food = new Food
        {
            Name = child.Name,
            NormalizedName = normalized,
            DefaultUoM = child.DefaultUoM,
            CaloriesPerUnit = child.CaloriesPerUnit,
            ProteinPerUnit = child.ProteinPerUnit,
            CarbsPerUnit = child.CarbsPerUnit,
            FatPerUnit = child.FatPerUnit,
        };
        db.Foods.Add(food);
        await db.SaveChangesAsync(ct);
        return food.Id;
    }

    internal static FoodResponse ToResponse(Food f) => new()
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
