namespace Api.Services;

/// <summary>
/// The one seam for AI calorie/macro estimation (text and photo). The provider is chosen
/// at deploy time (AI_* env keys); callers depend only on this interface. A single query
/// yields MANY line-items — see docs/ai-estimation.md. Every call takes a
/// <see cref="CancellationToken"/> so a user "Cancel" (or the per-call timeout) tears
/// down the in-flight provider request.
/// </summary>
public interface INutritionEstimator
{
    /// <summary>Whether this provider's model can actually see images (the photo path).</summary>
    bool SupportsImages { get; }

    Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct);

    Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct);
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
/// Thin composite that delegates text→DeepSeek, image→Claude (both are
/// <see cref="AnthropicEstimator"/> instances over different connections). If only one
/// provider is configured it serves both directions. Photos go to Claude because
/// DeepSeek's v4 models are text-only — their Anthropic-compatible endpoint accepts an
/// image but silently substitutes "[Unsupported Image]" before the model sees it, so it
/// returns nothing usable. <see cref="SupportsImages"/> reflects the image provider.
/// </summary>
public class CompositeNutritionEstimator(
    INutritionEstimator textProvider, INutritionEstimator imageProvider) : INutritionEstimator
{
    public bool SupportsImages => imageProvider.SupportsImages;

    public Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
        => textProvider.EstimateFromTextAsync(description, notes, ct);

    public Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
        => imageProvider.EstimateFromImageAsync(image, contentType, notes, ct);
}

/// <summary>
/// Raised when AI is disabled or the provider call fails after retry. The controller
/// catches it and returns a "couldn't estimate — enter it manually" response so
/// logging is never blocked (docs/ai-providers.md §Resilience).
/// </summary>
public class AiUnavailableException(string message) : Exception(message);
