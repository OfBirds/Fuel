using System.Net;
using Api.Config;
using Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Api.Tests;

/// <summary>
/// Verifies the OFF JSON → <see cref="BarcodeMatch"/> mapping. No real HTTP —
/// a stub <see cref="HttpMessageHandler"/> returns canned JSON.
/// </summary>
public class OpenFoodFactsLookupTests
{
    private static OpenFoodFactsLookup CreateLookup(
        HttpMessageHandler handler, bool enabled = true)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.off") };
        var factory = new FakeHttpClientFactory(client);
        var options = Options.Create(new BarcodeOptions
        {
            Enabled = enabled,
            BaseUrl = "https://test.off",
            TimeoutSeconds = 10,
        });
        var logger = LoggerFactory.Create(b => b.AddConsole())
            .CreateLogger<OpenFoodFactsLookup>();
        return new OpenFoodFactsLookup(factory, options, logger);
    }

    /// <summary>Happy path: a real OFF-shaped response with full nutrition.</summary>
    [Fact]
    public async Task Lookup_ValidProduct_ReturnsMatch()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
        {
          "status": 1,
          "product": {
            "product_name": "Nutella",
            "nutriments": {
              "energy-kcal_100g": 539,
              "proteins_100g": 6.3,
              "carbohydrates_100g": 57.5,
              "fat_100g": 30.6
            }
          }
        }
        """);

        var lookup = CreateLookup(handler);
        var match = await lookup.LookupAsync("3017620422003", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("Nutella", match!.Name);
        Assert.Equal(5.39, match.CaloriesPerGram);   // 539 ÷ 100
        Assert.Equal(0.063, match.ProteinPerGram);    // 6.3 ÷ 100
        Assert.Equal(0.575, match.CarbsPerGram);      // 57.5 ÷ 100
        Assert.Equal(0.306, match.FatPerGram);        // 30.6 ÷ 100
        Assert.Equal("OpenFoodFacts", match.Source);
    }

    /// <summary>status != 1 → null.</summary>
    [Fact]
    public async Task Lookup_StatusZero_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"status":0,"product":{"product_name":"Nope"}}""");
        var lookup = CreateLookup(handler);
        Assert.Null(await lookup.LookupAsync("000", CancellationToken.None));
    }

    /// <summary>Missing energy-kcal → null (required field).</summary>
    [Fact]
    public async Task Lookup_MissingEnergy_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
        {"status":1,"product":{"product_name":"No Energy","nutriments":{"proteins_100g":5}}}
        """);
        var lookup = CreateLookup(handler);
        Assert.Null(await lookup.LookupAsync("000", CancellationToken.None));
    }

    /// <summary>Non-2xx → null.</summary>
    [Fact]
    public async Task Lookup_ServerError_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        var lookup = CreateLookup(handler);
        Assert.Null(await lookup.LookupAsync("000", CancellationToken.None));
    }

    /// <summary>Empty product name → null.</summary>
    [Fact]
    public async Task Lookup_EmptyName_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
        {"status":1,"product":{"product_name":"","nutriments":{"energy-kcal_100g":100}}}
        """);
        var lookup = CreateLookup(handler);
        Assert.Null(await lookup.LookupAsync("000", CancellationToken.None));
    }

    /// <summary>Some fields nullable: protein/carbs/fat absent → still a match with nulls.</summary>
    [Fact]
    public async Task Lookup_OptionalFieldsMissing_ReturnsMatchWithNulls()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
        {"status":1,"product":{"product_name":"Just Energy","nutriments":{"energy-kcal_100g":200}}}
        """);
        var lookup = CreateLookup(handler);
        var match = await lookup.LookupAsync("000", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("Just Energy", match!.Name);
        Assert.Equal(2.0, match.CaloriesPerGram);
        Assert.Null(match.ProteinPerGram);
        Assert.Null(match.CarbsPerGram);
        Assert.Null(match.FatPerGram);
    }

    // ── Test doubles ──

    private class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
    }

    private class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
