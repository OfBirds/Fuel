namespace Api.DTOs;

/// <summary>Request to estimate nutrition from a typed description. `Notes` accumulates
/// the user's clarifications across the refine loop (empty on the first call).</summary>
public class EstimateTextRequest
{
    public string Description { get; set; } = "";
    public List<string>? Notes { get; set; }
}

/// <summary>
/// The estimate result mapped for the multi-row review screen. `Ok = false` carries a
/// human message and an empty item list (AI off / failure / timeout) — the UI then
/// falls back to manual entry. Nothing here is persisted until the user confirms.
/// </summary>
public class EstimateResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public double OverallConfidence { get; set; }
    public string Source { get; set; } = "AiText";
    public List<EstimateRow> Items { get; set; } = [];

    public static EstimateResponse Unavailable(string error) => new() { Ok = false, Error = error };
}

public class EstimateRow
{
    public required string Name { get; set; }
    public double Quantity { get; set; }
    public required string Uom { get; set; }
    public double Calories { get; set; }
    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }
    public double Confidence { get; set; }

    /// <summary>Set when the item resolved to an existing catalogue food (exact name match).</summary>
    public Guid? MatchedFoodId { get; set; }
    /// <summary>The matched food's default unit (for later unit-conversion hints).</summary>
    public string? MatchedDefaultUoM { get; set; }
    /// <summary>True when no catalogue match — the food is created on save, badged "new".</summary>
    public bool IsNew { get; set; }
}
