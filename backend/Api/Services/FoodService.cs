using Api.Data;
using Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class FoodService(AppDbContext db) : IFoodService
{
    public async Task<bool> WouldCreateCycle(Guid parentFoodId, Guid childFoodId, CancellationToken ct)
    {
        if (parentFoodId == childFoodId)
            return true;

        // BFS from childFoodId — if we ever reach parentFoodId through ingredient chains, it's a cycle.
        var visited = new HashSet<Guid> { childFoodId };
        var queue = new Queue<Guid>();
        queue.Enqueue(childFoodId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var childIds = await db.FoodIngredients
                .Where(fi => fi.ParentFoodId == current)
                .Select(fi => fi.ChildFoodId)
                .ToListAsync(ct);

            foreach (var cid in childIds)
            {
                if (cid == parentFoodId)
                    return true;
                if (visited.Add(cid))
                    queue.Enqueue(cid);
            }
        }

        return false;
    }

    public async Task<List<string>> ValidateIngredientsAsync(
        Guid parentFoodId, List<IngredientRequest> ingredients, CancellationToken ct)
    {
        var errors = new List<string>();

        for (int i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            if (ing.ChildFoodId.HasValue)
            {
                var food = await db.Foods.FindAsync([ing.ChildFoodId.Value], ct);
                if (food is null)
                {
                    errors.Add($"Ingredient [{i}]: food {ing.ChildFoodId} not found.");
                    continue;
                }

                if (await WouldCreateCycle(parentFoodId, ing.ChildFoodId.Value, ct))
                    errors.Add($"Ingredient [{i}] ({food.Name}): would create a circular reference.");
            }
            else if (ing.InlineChild is not null)
            {
                if (string.IsNullOrWhiteSpace(ing.InlineChild.Name))
                    errors.Add($"Ingredient [{i}]: inline child name is required.");
                if (string.IsNullOrWhiteSpace(ing.InlineChild.DefaultUoM))
                    errors.Add($"Ingredient [{i}]: inline child default UoM is required.");
            }
            else
            {
                errors.Add($"Ingredient [{i}]: must specify either childFoodId or inlineChild.");
            }
        }

        return errors;
    }
}
