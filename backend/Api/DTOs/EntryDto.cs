namespace Api.DTOs;

public class EntryResponse
{
    public Guid Id { get; set; }
    public Guid? FoodId { get; set; }
    public required string FoodName { get; set; }
    public DateTime IntakeAtUtc { get; set; }
    public required string MealType { get; set; }
    public double Quantity { get; set; }
    public required string UoM { get; set; }
    public double Calories { get; set; }
    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }
    public required string Source { get; set; }
    public double? AiConfidence { get; set; }
}

public class CreateEntryRequest
{
    public Guid? FoodId { get; set; }
    public required string FoodName { get; set; }
    public DateTime? IntakeAtUtc { get; set; }
    public required string MealType { get; set; }
    public double Quantity { get; set; }
    public required string UoM { get; set; }
    public double Calories { get; set; }
    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }
}

/// <summary>Save several reviewed rows at once (the AI multi-item confirm). Each row
/// becomes its own <c>FoodEntry</c>; a row with no <c>FoodId</c> defines a new
/// catalogue food first, then references it.</summary>
public class CreateEntriesBatchRequest
{
    public List<BatchEntryItem> Items { get; set; } = [];
}

public class BatchEntryItem
{
    /// <summary>Existing catalogue food. Null → create a new food from this row.</summary>
    public Guid? FoodId { get; set; }
    public required string FoodName { get; set; }
    public DateTime? IntakeAtUtc { get; set; }
    public required string MealType { get; set; }
    public double Quantity { get; set; }
    public required string UoM { get; set; }
    public double Calories { get; set; }
    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }
    public double? Confidence { get; set; }
    /// <summary>Manual / AiText / AiPhoto. Defaults to AiText.</summary>
    public string? Source { get; set; }
}

public class UpdateEntryRequest
{
    public Guid? FoodId { get; set; }
    public string? FoodName { get; set; }
    public DateTime? IntakeAtUtc { get; set; }
    public string? MealType { get; set; }
    public double? Quantity { get; set; }
    public string? UoM { get; set; }
    public double? Calories { get; set; }
    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }
}
