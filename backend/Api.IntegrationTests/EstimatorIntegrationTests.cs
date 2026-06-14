using System.Text.Json;
using Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Api.IntegrationTests;

/// <summary>
/// Integration tests that exercise <see cref="INutritionEstimator"/> implementations
/// against a real HTTP server (WireMock.Net) — verifying the full serialization,
/// HTTP call, and deserialization pipeline, not just in-memory stubs.
/// </summary>
[Collection("WireMock")]
public class EstimatorIntegrationTests(WireMockFixture wire)
{
    // ── Shared helpers ────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Valid estimate the parsers accept.</summary>
    private static string ValidEstimateJson => """
        {"items":[
          {"name":"banana","quantity":1,"uom":"pc","calories":105,"protein":1.3,"carbs":27,"fat":0.4,"confidence":0.9},
          {"name":"milk","quantity":200,"uom":"ml","calories":128,"protein":6.8,"carbs":10,"fat":4.8,"confidence":0.85}
        ],"overallConfidence":0.88}
        """.Replace("\n", "").Replace("  ", "");

    // The inner JSON must be embedded as a *string* in the outer wrapper,
    // so we serialize with System.Text.Json which handles escaping properly.

    private static string OpenAiOk(string innerJson) =>
        JsonSerializer.Serialize(new {
            choices = new[] { new { message = new { content = innerJson } } }
        }, Json);

    private static string AnthropicOk(string innerJson) =>
        JsonSerializer.Serialize(new {
            content = new[] { new { text = innerJson } }
        }, Json);

    private static string AnthropicWithThinking(string innerJson) =>
        JsonSerializer.Serialize(new {
            content = new object[] {
                new { type = "thinking" },
                new { text = innerJson },
            }
        }, Json);

    // ── OpenAI convention ─────────────────────────────────────────

    [Fact]
    public async Task OpenAiEstimator_Text_SuccessfulRoundTrip()
    {
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        var result = await estimator.EstimateFromTextAsync("a banana and a glass of milk", [], CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("banana", result.Items[0].Name);
        Assert.Equal(1, result.Items[0].Quantity);
        Assert.Equal("pc", result.Items[0].Uom);
        Assert.Equal(105, result.Items[0].Calories);
        Assert.Equal(0.9, result.Items[0].Confidence);
        Assert.Equal("milk", result.Items[1].Name);
        Assert.Equal(200, result.Items[1].Quantity);
        Assert.Equal("ml", result.Items[1].Uom);
    }

    [Fact]
    public async Task OpenAiEstimator_Text_WithApiKey_SendsBearer()
    {
        wire.Server.ResetLogEntries();
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true, apiKey: "sk-secret");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        await estimator.EstimateFromTextAsync("a banana", [], CancellationToken.None);

        var log = Assert.Single(wire.Server.LogEntries);
        Assert.Equal("Bearer sk-secret", log.RequestMessage.Headers?["Authorization"]?.ToString());
    }

    [Fact]
    public async Task OpenAiEstimator_Text_NoApiKey_OmitsAuth()
    {
        wire.Server.ResetLogEntries();
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true, apiKey: null);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        await estimator.EstimateFromTextAsync("a banana", [], CancellationToken.None);

        var log = Assert.Single(wire.Server.LogEntries);
        Assert.False(log.RequestMessage.Headers?.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task OpenAiEstimator_Image_SendsImagePayload()
    {
        wire.Server.ResetLogEntries();
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(OpenAiOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        var image = new byte[] { 1, 2, 3 };
        await estimator.EstimateFromImageAsync(image, "image/png", [], CancellationToken.None);

        var log = Assert.Single(wire.Server.LogEntries);
        var body = log.RequestMessage.Body;
        Assert.Contains("image_url", body);
        Assert.Contains("data:image/png;base64,AQID", body);
    }

    // ── Anthropic convention ──────────────────────────────────────

    [Fact]
    public async Task AnthropicEstimator_Text_SuccessfulRoundTrip()
    {
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        var result = await estimator.EstimateFromTextAsync("a banana and a glass of milk", [], CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("banana", result.Items[0].Name);
        Assert.Equal("milk", result.Items[1].Name);
    }

    [Fact]
    public async Task AnthropicEstimator_Text_SkipsThinkingBlock()
    {
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicWithThinking(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        var result = await estimator.EstimateFromTextAsync("a banana", [], CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task AnthropicEstimator_Image_SendsImageBlock()
    {
        wire.Server.ResetLogEntries();
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        var image = new byte[] { 1, 2, 3 };
        await estimator.EstimateFromImageAsync(image, "image/png", [], CancellationToken.None);

        var log = Assert.Single(wire.Server.LogEntries);
        var body = log.RequestMessage.Body;
        Assert.Contains("\"type\":\"image\"", body);
        Assert.Contains("AQID", body);
    }

    [Fact]
    public async Task AnthropicEstimator_SendsApiKeyAndVersion()
    {
        wire.Server.ResetLogEntries();
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicOk(ValidEstimateJson))
                .WithHeader("Content-Type", "application/json"));

        var conn = wire.Connection("test-model", supportsImages: true, apiKey: "sk-ant-secret");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        await estimator.EstimateFromTextAsync("a banana", [], CancellationToken.None);

        var log = Assert.Single(wire.Server.LogEntries);
        Assert.Equal("sk-ant-secret", log.RequestMessage.Headers?["x-api-key"]?.ToString());
        Assert.Equal("2023-06-01", log.RequestMessage.Headers?["anthropic-version"]?.ToString());
    }

    // ── Error handling ────────────────────────────────────────────

    [Fact]
    public async Task Estimator_ServerError_ThrowsHttpRequestException()
    {
        // Estimator itself does NOT catch HTTP errors — it lets the chain handle them.
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var conn = wire.Connection("test-model");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => estimator.EstimateFromTextAsync("food", [], CancellationToken.None));
    }

    [Fact]
    public async Task Estimator_MalformedInnerJson_ThrowsAiUnavailable()
    {
        // Valid outer wrapper, garbage inside — exercises the inner try/catch.
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicOk("this is not JSON at all")));

        var conn = wire.Connection("test-model");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        await Assert.ThrowsAsync<AiUnavailableException>(
            () => estimator.EstimateFromTextAsync("food", [], CancellationToken.None));
    }

    [Fact]
    public async Task Estimator_EmptyItems_ThrowsAiUnavailable()
    {
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicOk("""{"items":[],"overallConfidence":0}""")));

        var conn = wire.Connection("test-model");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        await Assert.ThrowsAsync<AiUnavailableException>(
            () => estimator.EstimateFromTextAsync("food", [], CancellationToken.None));
    }

    [Fact]
    public async Task Estimator_ValidationError_ThrowsHttpRequestException()
    {
        wire.Server
            .Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithBody("""{"error":{"message":"model not found"}}"""));

        var conn = wire.Connection("test-model");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => estimator.EstimateFromTextAsync("food", [], CancellationToken.None));
    }

    [Fact]
    public async Task Estimator_ConnectionRefused_Throws()
    {
        var conn = new ProviderConnection("http://127.0.0.1:1", null, "test-model", true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var estimator = new OpenAiEstimator(http, conn, NullLogger<OpenAiEstimator>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(
            () => estimator.EstimateFromTextAsync("food", [], CancellationToken.None));
    }

    [Fact]
    public async Task Estimator_EmptyNameItems_FilteredOut_ButValidOneSucceeds()
    {
        var inner = """
            {"items":[
              {"name":"","quantity":1,"uom":"g","calories":10,"confidence":0.5},
              {"name":"  ","quantity":1,"uom":"g","calories":10,"confidence":0.5},
              {"name":"bread","quantity":2,"uom":"pc","calories":200,"confidence":0.9}
            ],"overallConfidence":0.9}
            """.Replace("\n", "").Replace("  ", "");
        wire.Server
            .Given(Request.Create().WithPath("/v1/messages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(AnthropicOk(inner)));

        var conn = wire.Connection("test-model");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var estimator = new AnthropicEstimator(http, conn, NullLogger<AnthropicEstimator>.Instance);

        var result = await estimator.EstimateFromTextAsync("a bread", [], CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("bread", result.Items[0].Name);
    }
}
