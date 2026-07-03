namespace Api.DTOs;

public class FoodListItemResponse
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string DefaultUoM { get; set; }
    public double CaloriesPerUnit { get; set; }
    public int IngredientCount { get; set; }
    public bool IsComposite { get; set; }
    /// <summary>Current user's ponder value (null when no user context provided, or default 100).</summary>
    public int? Ponder { get; set; }
    /// <summary>Per-user usage count (null when no user context).</summary>
    public int? UsageCount { get; set; }
    /// <summary>Per-user last-used timestamp (null when no user context).</summary>
    public DateTime? LastUsedAtUtc { get; set; }
}

public class FoodResponse
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string DefaultUoM { get; set; }
    public double CaloriesPerUnit { get; set; }
    public double? ProteinPerUnit { get; set; }
    public double? CarbsPerUnit { get; set; }
    public double? FatPerUnit { get; set; }
    public string? IconRef { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<IngredientResponse> Ingredients { get; set; } = [];
    public bool IsComposite { get; set; }
}

public class IngredientResponse
{
    public Guid ChildFoodId { get; set; }
    public required string ChildFoodName { get; set; }
    public double Quantity { get; set; }
    public required string UoM { get; set; }
}

public class CreateFoodRequest
{
    public required string Name { get; set; }
    public required string DefaultUoM { get; set; }
    public double CaloriesPerUnit { get; set; }
    public double? ProteinPerUnit { get; set; }
    public double? CarbsPerUnit { get; set; }
    public double? FatPerUnit { get; set; }
    public List<IngredientRequest> Ingredients { get; set; } = [];
}

public class UpdateFoodRequest
{
    public required string Name { get; set; }
    public required string DefaultUoM { get; set; }
    public double CaloriesPerUnit { get; set; }
    public double? ProteinPerUnit { get; set; }
    public double? CarbsPerUnit { get; set; }
    public double? FatPerUnit { get; set; }
    public List<IngredientRequest> Ingredients { get; set; } = [];
}

public class IngredientRequest
{
    /// <summary>ID of an existing catalogue food. Mutually exclusive with InlineChild.</summary>
    public Guid? ChildFoodId { get; set; }

    /// <summary>Definition for a food created inline. Mutually exclusive with ChildFoodId.</summary>
    public InlineChildRequest? InlineChild { get; set; }

    public double Quantity { get; set; }
    public required string UoM { get; set; }
}

public class InlineChildRequest
{
    public required string Name { get; set; }
    public required string DefaultUoM { get; set; }
    public double CaloriesPerUnit { get; set; }
    public double? ProteinPerUnit { get; set; }
    public double? CarbsPerUnit { get; set; }
    public double? FatPerUnit { get; set; }
}

public class SetPriorityRequest
{
    /// <summary>Ponder value to set (0-10_000). Floor 0, default 100.</summary>
    public int Ponder { get; set; }
}
