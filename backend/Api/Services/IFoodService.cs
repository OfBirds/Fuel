using Api.DTOs;

namespace Api.Services;

public interface IFoodService
{
    /// <summary>Returns true if adding childFoodId as an ingredient of parentFoodId would create a cycle.</summary>
    Task<bool> WouldCreateCycle(Guid parentFoodId, Guid childFoodId, CancellationToken ct);

    /// <summary>Validates all ingredient requests, checking for cycles and missing foods.</summary>
    Task<List<string>> ValidateIngredientsAsync(Guid parentFoodId, List<IngredientRequest> ingredients, CancellationToken ct);
}
