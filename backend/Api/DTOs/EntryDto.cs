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
