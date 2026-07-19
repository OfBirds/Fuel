using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

/// <summary>
/// AnthropicEstimator against a stubbed Messages API (no live calls). Covers the parsing
/// that makes one implementation work for BOTH providers: DeepSeek's Anthropic-compatible
/// endpoint prepends a "thinking" content block before the answer, and either provider may
/// fence the JSON. Also checks the image request actually carries an image block.
/// </summary>
public class AnthropicEstimatorTests
{
    private static AnthropicEstimator Estimator(string responseBody, List<string>? capturedBodies = null)
    {
        var handler = new StubHandler(responseBody, capturedBodies);
        var http = new HttpClient(handler);
        return new AnthropicEstimator(http,
            new ProviderConnection("https://example.test", "test-key", "test-model", SupportsImages: true),
            NullLogger<AnthropicEstimator>.Instance);
    }

    private static string Reply(params object[] contentBlocks)
    {
        // Minimal Anthropic Messages API response: { "content": [ ...blocks... ] }
        var blocks = string.Join(",", contentBlocks);
        return $"{{\"content\":[{blocks}]}}";
    }

    private const string ItemsJson =
        "{\\\"items\\\":[{\\\"name\\\":\\\"Pizza\\\",\\\"quantity\\\":150,\\\"uom\\\":\\\"g\\\"," +
        "\\\"calories\\\":400,\\\"protein\\\":18,\\\"carbs\\\":45,\\\"fat\\\":16,\\\"confidence\\\":0.8}]," +
        "\\\"overallConfidence\\\":0.8}";

    [Fact]
    public async Task Parse_SkipsThinkingBlock_FromReasonerEndpoint()
    {
        // DeepSeek's /anthropic endpoint returns a "thinking" block before the answer.
        var body = Reply(
            "{\"type\":\"thinking\",\"thinking\":\"Let me look at the food...\"}",
            $"{{\"type\":\"text\",\"text\":\"{ItemsJson}\"}}");

        var result = await Estimator(body).EstimateFromTextAsync("pizza", [], default);

        var item = Assert.Single(result.Items);
        Assert.Equal("Pizza", item.Name);
        Assert.Equal(400, item.Calories);
        Assert.Equal(0.8, result.OverallConfidence);
    }

    [Fact]
    public async Task Parse_StripsCodeFences()
    {
        var fenced = "```json\\n" + ItemsJson + "\\n```";
        var body = Reply($"{{\"type\":\"text\",\"text\":\"{fenced}\"}}");

        var result = await Estimator(body).EstimateFromTextAsync("pizza", [], default);

        Assert.Equal("Pizza", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task Parse_NoTextBlock_Throws()
    {
        // Only a thinking block, no answer → unusable.
        var body = Reply("{\"type\":\"thinking\",\"thinking\":\"hmm\"}");

        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            Estimator(body).EstimateFromTextAsync("x", [], default));
    }

    [Fact]
    public async Task ImageRequest_SendsImageBlock()
    {
        var captured = new List<string>();
        var body = Reply($"{{\"type\":\"text\",\"text\":\"{ItemsJson}\"}}");

        await Estimator(body, capturedBodies: captured)
            .EstimateFromImageAsync([1, 2, 3], "image/png", null, [], default);

        var sent = Assert.Single(captured);
        Assert.Contains("\"type\":\"image\"", sent);
        Assert.Contains("image/png", sent);
        Assert.Contains(Convert.ToBase64String([1, 2, 3]), sent);
    }

    [Fact]
    public async Task ImageRequest_WithDescription_IncludesItInPrompt()
    {
        var captured = new List<string>();
        var body = Reply($"{{\"type\":\"text\",\"text\":\"{ItemsJson}\"}}");

        await Estimator(body, capturedBodies: captured)
            .EstimateFromImageAsync([1, 2, 3], "image/png", "leftover pad thai", [], default);

        Assert.Contains("leftover pad thai", Assert.Single(captured));
    }

    [Fact]
    public async Task RateLimited_ThrowsAiRateLimitedException_WithRetryAfter()
    {
        var http = new HttpClient(new StatusHandler(HttpStatusCode.TooManyRequests, retryAfterSeconds: 30));
        var estimator = new AnthropicEstimator(http,
            new ProviderConnection("https://example.test", "test-key", "test-model", SupportsImages: true),
            NullLogger<AnthropicEstimator>.Instance);

        var ex = await Assert.ThrowsAsync<AiRateLimitedException>(() =>
            estimator.EstimateFromTextAsync("pizza", [], default));

        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task RateLimited_NoRetryAfterHeader_LeavesRetryAfterNull()
    {
        var http = new HttpClient(new StatusHandler(HttpStatusCode.TooManyRequests));
        var estimator = new AnthropicEstimator(http,
            new ProviderConnection("https://example.test", "test-key", "test-model", SupportsImages: true),
            NullLogger<AnthropicEstimator>.Instance);

        var ex = await Assert.ThrowsAsync<AiRateLimitedException>(() =>
            estimator.EstimateFromTextAsync("pizza", [], default));

        Assert.Null(ex.RetryAfter);
    }

    private sealed class StubHandler(string responseBody, List<string>? capturedBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (capturedBodies is not null && request.Content is not null)
                capturedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StatusHandler(HttpStatusCode status, int? retryAfterSeconds = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"type\":\"error\",\"error\":{\"message\":\"rate limited\"}}",
                    Encoding.UTF8, "application/json"),
            };
            if (retryAfterSeconds is { } s)
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return Task.FromResult(resp);
        }
    }
}
