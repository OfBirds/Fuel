namespace Api.Models;

public class FoodEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Reference to the catalogue food. Null if the food was deleted.</summary>
    public Guid? FoodId { get; set; }
    public Food? Food { get; set; }

    /// <summary>Snapshotted food name so history is readable even after deletion.</summary>
    public required string FoodName { get; set; }

    public DateTime IntakeAtUtc { get; set; } = DateTime.UtcNow;

    public MealType MealType { get; set; }

    public double Quantity { get; set; }
    public required string UoM { get; set; }

    /// <summary>Snapshotted calories at log time.</summary>
    public double Calories { get; set; }

    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }

    public EntrySource Source { get; set; } = EntrySource.Manual;
    public double? AiConfidence { get; set; }
}

public enum EntrySource
{
    Manual,
    AiText,
    AiPhoto
}
