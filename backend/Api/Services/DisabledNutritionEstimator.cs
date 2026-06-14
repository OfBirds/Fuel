namespace Api.Services;

/// <summary>
/// Registered when AI_ENABLED is false (or the provider/key is missing). Every call
/// fails fast so the controller returns the manual-fallback response and the UI shows
/// AI affordances as off. Keeps DI resolvable without a real provider configured.
/// </summary>
public class DisabledNutritionEstimator : INutritionEstimator
{
    public bool SupportsImages => false;

    public Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
        => throw new AiUnavailableException("AI estimation is turned off.");

    public Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
        => throw new AiUnavailableException("AI estimation is turned off.");
}
