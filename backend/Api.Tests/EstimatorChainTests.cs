using System.Net;
using System.Text;
using Api.Config;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Api.Tests;

/// <summary>
/// EstimatorChain — the ordered, per-modality fallback. Providers come from a fake options
/// monitor; HTTP is stubbed and routed by host so we can make specific providers fail. No
/// live calls. This is the headline behaviour: local-first, cloud-fallback, capability-gated.
/// </summary>
public class EstimatorChainTests
{
    [Fact]
    public void Capabilities_DerivedFromEnabledProviders()
    {
        var chain = Chain(
            Provider("local", "openai", ["text", "vision"], "http://local.test/v1", order: 1),
            Provider("cloud", "anthropic", ["text"], "https://cloud.test", order: 2, keyRef: "cloud"));

        Assert.True(chain.SupportsText);
        Assert.True(chain.SupportsImages); // only "local" has vision
    }

    [Fact]
    public void NoVisionProvider_SupportsImagesFalse()
    {
        var chain = Chain(Provider("t", "openai", ["text"], "http://t.test/v1", order: 1));
        Assert.True(chain.SupportsText);
        Assert.False(chain.SupportsImages);
    }

    [Fact]
    public async Task EmptyChain_ThrowsNotConfigured()
    {
        var chain = Chain(); // nothing configured
        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            chain.EstimateFromTextAsync("x", [], default));
    }

    [Fact]
    public async Task FallsThroughToNextProvider_OnFailure()
    {
        // order 1 = local (fails 500), order 2 = cloud (succeeds). Expect cloud's result.
        var chain = Chain(
            Provider("local", "openai", ["text", "vision"], "http://local.test/v1", order: 1),
            Provider("cloud", "anthropic", ["text", "vision"], "https://cloud.test", order: 2, keyRef: "cloud"));

        var result = await chain.EstimateFromTextAsync("pizza", [], default);

        Assert.Equal("Pizza", Assert.Single(result.Items).Name);
        Assert.Contains(_hits, h => h.Contains("local.test"));  // tried first
        Assert.Contains(_hits, h => h.Contains("cloud.test"));  // then fell through
    }

    [Fact]
    public async Task DisabledProvider_IsSkipped()
    {
        // local would win on order but is disabled → only cloud is tried.
        var chain = Chain(
            Provider("local", "openai", ["text"], "http://local.test/v1", order: 1, enabled: false),
            Provider("cloud", "anthropic", ["text"], "https://cloud.test", order: 2, keyRef: "cloud"));

        var result = await chain.EstimateFromTextAsync("pizza", [], default);

        Assert.Single(result.Items);
        Assert.DoesNotContain(_hits, h => h.Contains("local.test"));
        Assert.Contains(_hits, h => h.Contains("cloud.test"));
    }

    [Fact]
    public async Task RateLimitedProvider_CoolsDown_SkippedOnSubsequentCall()
    {
        // order 1 = limited (429), order 2 = cloud (succeeds).
        var chain = Chain(
            Provider("limited", "anthropic", ["text"], "https://limited.test", order: 1, keyRef: "cloud"),
            Provider("cloud", "anthropic", ["text"], "https://cloud.test", order: 2, keyRef: "cloud"));

        var first = await chain.EstimateFromTextAsync("pizza", [], default);
        Assert.Single(first.Items);
        Assert.Equal(1, _hits.Count(h => h.Contains("limited.test")));
        Assert.Equal(1, _hits.Count(h => h.Contains("cloud.test")));

        // Still cooling down — skipped without another HTTP call; cloud is hit directly.
        var second = await chain.EstimateFromTextAsync("pizza", [], default);
        Assert.Single(second.Items);
        Assert.Equal(1, _hits.Count(h => h.Contains("limited.test"))); // not retried
        Assert.Equal(2, _hits.Count(h => h.Contains("cloud.test")));
    }

    [Fact]
    public async Task AllProvidersFail_ThrowsAfterTryingAll()
    {
        var chain = Chain(
            Provider("local", "openai", ["text"], "http://local.test/v1", order: 1),
            Provider("other", "openai", ["text"], "http://local.test/v1", order: 2)); // both route to failing host

        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            chain.EstimateFromTextAsync("x", [], default));
        Assert.Equal(2, _hits.Count(h => h.Contains("local.test"))); // both attempted
    }

    // ── harness ──

    private readonly List<string> _hits = [];

    private EstimatorChain Chain(params AiProvider[] providers)
    {
        var opts = new TestMonitor(new AiProvidersOptions { Providers = [.. providers] });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI_KEY_CLOUD"] = "secret" })
            .Build();
        return new EstimatorChain(opts, config, new RoutingFactory(_hits),
            new NullLoggerFactory(), NullLogger<EstimatorChain>.Instance);
    }

    private static AiProvider Provider(string name, string convention, List<string> caps,
        string baseUrl, int order, bool enabled = true, string? keyRef = null)
        => new()
        {
            Name = name, Convention = convention, Capabilities = caps,
            BaseUrl = baseUrl, Model = "m", Order = order, Enabled = enabled, KeyRef = keyRef,
        };

    private sealed class TestMonitor(AiProvidersOptions value) : IOptionsMonitor<AiProvidersOptions>
    {
        public AiProvidersOptions CurrentValue { get; } = value;
        public AiProvidersOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AiProvidersOptions, string?> listener) => null;
    }

    // Routes by host: local.test → 500 (fail), limited.test → 429, cloud.test → a valid
    // Anthropic-shaped reply.
    private sealed class RoutingFactory(List<string> hits) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RoutingHandler(hits));
    }

    private sealed class RoutingHandler(List<string> hits) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            hits.Add(url);
            if (url.Contains("cloud.test"))
            {
                const string items =
                    "{\"items\":[{\"name\":\"Pizza\",\"quantity\":150,\"uom\":\"g\",\"calories\":400," +
                    "\"protein\":18,\"carbs\":45,\"fat\":16,\"confidence\":0.8}],\"overallConfidence\":0.8}";
                var anthropic = $"{{\"content\":[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(items)}}}]}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(anthropic, Encoding.UTF8, "application/json"),
                });
            }
            if (url.Contains("limited.test"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"type\":\"error\"}", Encoding.UTF8, "application/json"),
                };
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return Task.FromResult(resp);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        }
    }
}
