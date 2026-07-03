using System.Text.Json;
using Api.Config;
using Microsoft.Extensions.Options;

namespace Api.Services;

/// <summary>
/// Resolves product barcodes via the Open Food Facts public API (no key required).
/// Maps per-100g nutrition to per-gram values. Time-boxed via the named HttpClient's
/// resilience pipeline; any error/timeout/missing data → null (miss), never throws.
/// </summary>
public class OpenFoodFactsLookup : IBarcodeFoodLookup
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenFoodFactsLookup> _logger;

    public OpenFoodFactsLookup(
        IHttpClientFactory httpFactory,
        IOptions<BarcodeOptions> options,
        ILogger<OpenFoodFactsLookup> logger)
    {
        _http = httpFactory.CreateClient("barcode");
        _http.BaseAddress = new Uri(options.Value.BaseUrl);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Fuel/1.0 (self-hosted calorie tracker; https://github.com/OfBirds/Fuel)");
        Enabled = options.Value.Enabled;
        _logger = logger;
    }

    public bool Enabled { get; }

    public async Task<BarcodeMatch?> LookupAsync(string barcode, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"/api/v2/product/{barcode}.json", ct);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 1)
                return null;

            if (!root.TryGetProperty("product", out var product))
                return null;

            // Required: a non-empty product name
            var name = SafeString(product, "product_name");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            // Required: energy in kcal per 100g
            if (!product.TryGetProperty("nutriments", out var n)
                || !TryGetDouble(n, "energy-kcal_100g", out var kcal100))
                return null;

            // Per-gram → ÷100. Store at our catalogue convention (per g).
            const string Source = "OpenFoodFacts";
            var cal = Math.Round(kcal100 / 100, 3);
            double? prot = TryGetDouble(n, "proteins_100g", out var p100) ? Math.Round(p100 / 100, 3) : null;
            double? carbs = TryGetDouble(n, "carbohydrates_100g", out var c100) ? Math.Round(c100 / 100, 3) : null;
            double? fat = TryGetDouble(n, "fat_100g", out var f100) ? Math.Round(f100 / 100, 3) : null;

            _logger.LogInformation("Barcode lookup {Barcode} → {Name} ({Kcal} kcal/100g, source={Source})",
                barcode, name, kcal100, Source);

            return new BarcodeMatch(name, cal, prot, carbs, fat, Source);
        }
        catch (Exception ex)
        {
            // Resilience (spec §): any failure → null, caller falls back. Log the code
            // but no PII — just the digits.
            _logger.LogWarning(ex, "Barcode lookup failed for {Barcode}", barcode);
            return null;
        }
    }

    private static string? SafeString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    private static bool TryGetDouble(JsonElement el, string prop, out double value)
    {
        value = 0;
        return el.TryGetProperty(prop, out var v)
            && v.ValueKind != JsonValueKind.Null
            && v.TryGetDouble(out value)
            && !double.IsNaN(value)
            && !double.IsInfinity(value);
    }
}
