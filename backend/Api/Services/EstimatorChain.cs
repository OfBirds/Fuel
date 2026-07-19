using System.Collections.Concurrent;
using Api.Config;
using Microsoft.Extensions.Options;

namespace Api.Services;

/// <summary>What the controller depends on: capability flags + the two estimate entry points.
/// Faked in tests so no live provider is hit.</summary>
public interface IEstimatorChain
{
    bool SupportsText { get; }
    bool SupportsImages { get; }
    Task<NutritionEstimate> EstimateFromTextAsync(string description, IReadOnlyList<string> notes, CancellationToken ct);
    Task<NutritionEstimate> EstimateFromImageAsync(byte[] image, string contentType, string? description, IReadOnlyList<string> notes, CancellationToken ct);
}

/// <summary>
/// Routes an estimate through the configured providers in priority order. For the requested
/// modality (text/vision) it takes every enabled provider with that capability, sorted by
/// <see cref="AiProvider.Order"/> ascending, and tries them in turn: first success wins; any
/// failure (timeout, network, 5xx, bad JSON, or an empty/unusable result — including
/// DeepSeek's "[Unsupported Image]" no-op) falls through to the next. A user cancel
/// propagates and stops the chain. If every provider fails, the last error surfaces as
/// <see cref="AiUnavailableException"/> (→ manual fallback). Provider list comes from a
/// hot-reloaded <see cref="IOptionsMonitor{T}"/>, so reorder/enable/model edits take effect
/// live; secret key VALUES are resolved per-request from AI_KEY_&lt;NAME&gt; env vars.
///
/// A 429 (<see cref="AiRateLimitedException"/>) is treated specially: rather than retry
/// the same provider, it puts that provider in a per-instance cooldown (honouring the
/// response's Retry-After when present, else <see cref="DefaultRateLimitCooldown"/>) and
/// falls through immediately. Later calls skip a cooling-down provider outright instead of
/// hitting an already-limited API again — this instance is registered as a singleton, so
/// the cooldown is shared across requests.
/// </summary>
public class EstimatorChain(
    IOptionsMonitor<AiProvidersOptions> options,
    IConfiguration config,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    ILogger<EstimatorChain> logger) : IEstimatorChain
{
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldownUntil = new();

    public bool SupportsText => Chain("text").Count != 0;
    public bool SupportsImages => Chain("vision").Count != 0;

    public Task<NutritionEstimate> EstimateFromTextAsync(
        string description, IReadOnlyList<string> notes, CancellationToken ct)
        => RunChainAsync("text", est => est.EstimateFromTextAsync(description, notes, ct), ct);

    public Task<NutritionEstimate> EstimateFromImageAsync(
        byte[] image, string contentType, string? description, IReadOnlyList<string> notes, CancellationToken ct)
        => RunChainAsync("vision", est => est.EstimateFromImageAsync(image, contentType, description, notes, ct), ct);

    /// <summary>Enabled, capability-matching providers with a base URL, ordered (lowest first).</summary>
    private List<AiProvider> Chain(string capability)
        => options.CurrentValue.Providers
            .Where(p => p.Enabled && p.Has(capability) && !string.IsNullOrWhiteSpace(p.BaseUrl))
            .OrderBy(p => p.Order)
            .ToList();

    private async Task<NutritionEstimate> RunChainAsync(
        string capability, Func<INutritionEstimator, Task<NutritionEstimate>> call, CancellationToken ct)
    {
        var providers = Chain(capability);
        if (providers.Count == 0)
            throw new AiUnavailableException($"No provider configured for {capability}.");

        Exception? last = null;
        foreach (var p in providers)
        {
            ct.ThrowIfCancellationRequested();

            if (_cooldownUntil.TryGetValue(p.Name, out var until) && until > DateTimeOffset.UtcNow)
            {
                last = new AiRateLimitedException($"{p.Name} is cooling down after a rate limit.");
                logger.LogInformation(
                    "AI provider {Provider} ({Capability}) skipped — rate-limit cooldown for {Seconds}s more",
                    p.Name, capability, Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds));
                continue;
            }

            try
            {
                return await call(BuildConnector(p, supportsImages: capability == "vision"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancelled — stop the whole chain, don't fall through
            }
            catch (AiRateLimitedException ex)
            {
                var cooldown = ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                    ? retryAfter
                    : DefaultRateLimitCooldown;
                _cooldownUntil[p.Name] = DateTimeOffset.UtcNow + cooldown;
                last = ex;
                logger.LogWarning(ex,
                    "AI provider {Provider} ({Capability}) rate-limited; cooling down {Seconds}s, trying next",
                    p.Name, capability, cooldown.TotalSeconds);
            }
            catch (Exception ex)
            {
                last = ex;
                logger.LogWarning(ex, "AI provider {Provider} ({Capability}) failed; trying next", p.Name, capability);
            }
        }

        throw new AiUnavailableException(
            $"All {providers.Count} provider(s) for {capability} failed.", last);
    }

    private INutritionEstimator BuildConnector(AiProvider p, bool supportsImages)
    {
        var apiKey = string.IsNullOrWhiteSpace(p.KeyRef)
            ? null
            : config[$"AI_KEY_{p.KeyRef.Trim().ToUpperInvariant()}"];
        var connection = new ProviderConnection(p.BaseUrl, apiKey, p.Model, supportsImages);
        var http = httpClientFactory.CreateClient("ai");

        return p.Convention.Trim().ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicEstimator(http, connection, loggerFactory.CreateLogger<AnthropicEstimator>()),
            "openai" => new OpenAiEstimator(http, connection, loggerFactory.CreateLogger<OpenAiEstimator>()),
            var other => throw new AiUnavailableException($"Unknown provider convention '{other}' for {p.Name}."),
        };
    }
}
