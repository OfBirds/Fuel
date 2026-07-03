using Api.Config;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Api.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="EstimatorChain"/> — the ordered multi-provider
/// registry with fallback. Exercises the real HTTP path (via WireMock.Net) so the
/// fall-through, capability filtering, and "all failed" surfacing are validated
/// end-to-end, not against in-memory stubs.
/// </summary>
[Collection("WireMock")]
public class EstimatorChainIntegrationTests
{
    private readonly WireMockFixture wire;

    public EstimatorChainIntegrationTests(WireMockFixture wire)
    {
        this.wire = wire;
        wire.Server.Reset(); // shared collection fixture — start each test with clean mappings
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static string ValidEstimateJson => """
        {"items":[{"name":"banana","quantity":1,"uom":"pc","calories":105,"confidence":0.9}],"overallConfidence":0.9}
        """;

    private static string OpenAiOk(string innerJson) =>
        System.Text.Json.JsonSerializer.Serialize(new {
            choices = new[] { new { message = new { content = innerJson } } }
        });

    private static EstimatorChain BuildChain(params AiProvider[] providers)
    {
        var options = new StaticOptionsMonitor(new AiProvidersOptions { Providers = providers.ToList() });
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        return new EstimatorChain(
            options, config, new SimpleHttpClientFactory(),
            NullLoggerFactory.Instance, NullLogger<EstimatorChain>.Instance);
    }

    private static AiProvider Provider(string name, string baseUrl, int order,
        string[]? capabilities = null, bool enabled = true) =>
        new()
        {
            Name = name,
            Convention = "openai",
            Capabilities = (capabilities ?? ["text"]).ToList(),
            BaseUrl = baseUrl,
            Model = "test-model",
            Order = order,
            Enabled = enabled,
        };

    /// <summary>A base URL that always refuses the connection (port 1).</summary>
    private const string Unreachable = "http://127.0.0.1:1";

    // ── Tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task EstimateFromText_FirstProviderFails_FallsThroughToSecond()
    {
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var chain = BuildChain(
            Provider("broken", Unreachable, order: 0),           // tried first, connection refused
            Provider("healthy", wire.Server.Url!, order: 1));     // tried next, succeeds

        var result = await chain.EstimateFromTextAsync("a banana", [], CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("banana", result.Items[0].Name);
        // The healthy provider was actually reached.
        Assert.Single(wire.Server.LogEntries);
    }

    [Fact]
    public async Task EstimateFromText_OrderRespected_LowerOrderWins()
    {
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        // Healthy provider has the lower Order, so the unreachable one is never tried.
        var chain = BuildChain(
            Provider("healthy", wire.Server.Url!, order: 0),
            Provider("broken", Unreachable, order: 1));

        var result = await chain.EstimateFromTextAsync("a banana", [], CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Single(wire.Server.LogEntries); // exactly one upstream call — fallback not used
    }

    [Fact]
    public async Task EstimateFromText_AllProvidersFail_ThrowsAiUnavailable()
    {
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var chain = BuildChain(
            Provider("broken", Unreachable, order: 0),
            Provider("five-hundred", wire.Server.Url!, order: 1));

        await Assert.ThrowsAsync<AiUnavailableException>(
            () => chain.EstimateFromTextAsync("a banana", [], CancellationToken.None));
    }

    [Fact]
    public async Task TextOnlyProviders_VisionRequest_ReportsNoImageSupportAndThrows()
    {
        var chain = BuildChain(
            Provider("text-only", wire.Server.Url!, order: 0, capabilities: ["text"]));

        Assert.True(chain.SupportsText);
        Assert.False(chain.SupportsImages);

        // No vision-capable provider configured → the image chain is empty → AiUnavailable,
        // and nothing is sent upstream.
        await Assert.ThrowsAsync<AiUnavailableException>(
            () => chain.EstimateFromImageAsync([1, 2, 3], "image/png", null, [], CancellationToken.None));
        Assert.Empty(wire.Server.LogEntries);
    }

    [Fact]
    public async Task DisabledProvider_IsSkipped()
    {
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        // The only enabled provider is unreachable; the healthy one is disabled, so the
        // chain must fail rather than silently use it.
        var chain = BuildChain(
            Provider("broken", Unreachable, order: 0, enabled: true),
            Provider("healthy-but-off", wire.Server.Url!, order: 1, enabled: false));

        await Assert.ThrowsAsync<AiUnavailableException>(
            () => chain.EstimateFromTextAsync("a banana", [], CancellationToken.None));
        Assert.Empty(wire.Server.LogEntries); // disabled provider never reached
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> returning a fixed value (no reload).</summary>
file sealed class StaticOptionsMonitor(AiProvidersOptions value) : IOptionsMonitor<AiProvidersOptions>
{
    public AiProvidersOptions CurrentValue => value;
    public AiProvidersOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<AiProvidersOptions, string?> listener) => null;
}

/// <summary>Factory that hands out a short-timeout <see cref="HttpClient"/> per request.</summary>
file sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
}
