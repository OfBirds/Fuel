namespace Api.Services;

/// <summary>
/// One provider connection: the endpoint, the (optional) API key, the model, and whether
/// that model can see images. This is the only thing that differs between providers — the
/// <see cref="EstimatorChain"/> builds one per configured <see cref="Api.Config.AiProvider"/>
/// and hands it to the matching convention's estimator.
/// </summary>
public record ProviderConnection(string BaseUrl, string? ApiKey, string Model, bool SupportsImages);

/// <summary>
/// The seam for a single AI calorie/macro estimator over one connection (text + photo). A
/// single query yields MANY line-items — see docs/ai-estimation.md. Every call takes a
/// <see cref="CancellationToken"/> so a user "Cancel" (or the per-call timeout) tears down
/// the in-flight provider request. Concrete impls: <see cref="AnthropicEstimator"/> and
/// <see cref="OpenAiEstimator"/>, one per wire convention.
/// </summary>
public interface INutritionEstimator
{
    Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct);

    /// <summary><paramref name="description"/> is the optional free-text hint the user attaches
    /// to the photo on the first estimate; it is folded into the initial prompt so the model
    /// weighs it together with the image. Null/blank when the user just snaps a photo.</summary>
    Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, string? description, IReadOnlyList<string> notes, CancellationToken ct);
}

/// <summary>Prompt fragments shared by the concrete estimators so the wording of the
/// image instruction (and how the optional user description is woven in) stays identical
/// across the OpenAI and Anthropic conventions.</summary>
internal static class EstimatePrompts
{
    public static string ImageInstruction(string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? "Estimate the food in this photo."
            : "Estimate the food in this photo. The user adds this context, which should inform "
              + $"your estimate: {description.Trim()}";
}

/// <summary>One estimate = a list of editable line-items plus an overall confidence.</summary>
public class NutritionEstimate
{
    public List<EstimatedItem> Items { get; set; } = [];
    public double OverallConfidence { get; set; }
}

public class EstimatedItem
{
    public required string Name { get; set; }
    public double Quantity { get; set; }
    public required string Uom { get; set; }
    public double Calories { get; set; }
    public double? Protein { get; set; }
    public double? Carbs { get; set; }
    public double? Fat { get; set; }
    /// <summary>Per-item confidence, 0..1.</summary>
    public double Confidence { get; set; }
}

/// <summary>
/// Raised when a provider call fails (disabled, transient failure after retry, bad JSON, or
/// an empty/unusable result). The <see cref="EstimatorChain"/> catches it to fall through to
/// the next provider; the controller catches it to degrade to manual entry
/// (docs/ai-providers.md §Resilience).
/// </summary>
public class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
