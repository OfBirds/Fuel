using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

/// <summary>
/// OpenAiEstimator against a stubbed chat/completions API (no live calls). Covers JSON parse
/// + fence stripping, the OpenAI `image_url` vision content part, and that the Bearer header
/// is omitted when the connection has no key (local servers like Ollama).
/// </summary>
public class OpenAiEstimatorTests
{
    private static OpenAiEstimator Estimator(string content, string? apiKey = "k",
        List<HttpRequestMessage>? captured = null, List<string>? capturedBodies = null)
    {
        var body = $"{{\"choices\":[{{\"message\":{{\"content\":\"{content}\"}}}}]}}";
        var http = new HttpClient(new StubHandler(body, captured, capturedBodies));
        return new OpenAiEstimator(http,
            new ProviderConnection("http://ollama.test/v1", apiKey, "qwen2.5-vl", SupportsImages: true),
            NullLogger<OpenAiEstimator>.Instance);
    }

    private const string ItemsJson =
        "{\\\"items\\\":[{\\\"name\\\":\\\"Pizza\\\",\\\"quantity\\\":150,\\\"uom\\\":\\\"g\\\"," +
        "\\\"calories\\\":400,\\\"protein\\\":18,\\\"carbs\\\":45,\\\"fat\\\":16,\\\"confidence\\\":0.8}]," +
        "\\\"overallConfidence\\\":0.8}";

    [Fact]
    public async Task Parse_PlainJson()
    {
        var result = await Estimator(ItemsJson).EstimateFromTextAsync("pizza", [], default);
        Assert.Equal("Pizza", Assert.Single(result.Items).Name);
        Assert.Equal(0.8, result.OverallConfidence);
    }

    [Fact]
    public async Task Parse_StripsCodeFences()
    {
        var fenced = "```json\\n" + ItemsJson + "\\n```";
        var result = await Estimator(fenced).EstimateFromTextAsync("pizza", [], default);
        Assert.Equal("Pizza", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task ImageRequest_SendsImageUrlPart()
    {
        var bodies = new List<string>();
        await Estimator(ItemsJson, capturedBodies: bodies)
            .EstimateFromImageAsync([1, 2, 3], "image/png", null, [], default);

        var sent = Assert.Single(bodies);
        Assert.Contains("\"type\":\"image_url\"", sent);
        Assert.Contains("data:image/png;base64,", sent);
        Assert.Contains(Convert.ToBase64String([1, 2, 3]), sent);
    }

    [Fact]
    public async Task ImageRequest_WithDescription_IncludesItInPrompt()
    {
        var bodies = new List<string>();
        await Estimator(ItemsJson, capturedBodies: bodies)
            .EstimateFromImageAsync([1, 2, 3], "image/png", "grilled salmon with quinoa", [], default);

        Assert.Contains("grilled salmon with quinoa", Assert.Single(bodies));
    }

    [Fact]
    public async Task NoApiKey_OmitsAuthHeader()
    {
        var reqs = new List<HttpRequestMessage>();
        await Estimator(ItemsJson, apiKey: null, captured: reqs)
            .EstimateFromTextAsync("pizza", [], default);

        Assert.Null(Assert.Single(reqs).Headers.Authorization);
    }

    [Fact]
    public async Task WithApiKey_SendsBearer()
    {
        var reqs = new List<HttpRequestMessage>();
        await Estimator(ItemsJson, apiKey: "secret", captured: reqs)
            .EstimateFromTextAsync("pizza", [], default);

        var auth = Assert.Single(reqs).Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("secret", auth.Parameter);
    }

    [Fact]
    public async Task RateLimited_ThrowsAiRateLimitedException_WithRetryAfter()
    {
        var http = new HttpClient(new StatusHandler(HttpStatusCode.TooManyRequests, retryAfterSeconds: 15));
        var estimator = new OpenAiEstimator(http,
            new ProviderConnection("http://ollama.test/v1", "k", "qwen2.5-vl", SupportsImages: true),
            NullLogger<OpenAiEstimator>.Instance);

        var ex = await Assert.ThrowsAsync<AiRateLimitedException>(() =>
            estimator.EstimateFromTextAsync("pizza", [], default));

        Assert.Equal(TimeSpan.FromSeconds(15), ex.RetryAfter);
    }

    private sealed class StubHandler(
        string responseBody, List<HttpRequestMessage>? captured, List<string>? capturedBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captured?.Add(request);
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
                Content = new StringContent("{\"error\":{\"message\":\"rate limited\"}}", Encoding.UTF8, "application/json"),
            };
            if (retryAfterSeconds is { } s)
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return Task.FromResult(resp);
        }
    }
}
