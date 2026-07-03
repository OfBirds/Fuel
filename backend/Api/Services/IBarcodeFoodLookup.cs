namespace Api.Services;

/// <summary>A food definition resolved from a product barcode via an external
/// database (e.g. Open Food Facts). Values are per-gram so they slot directly into
/// the <see cref="Models.Food"/> per-unit convention (default UoM = "g").</summary>
public record BarcodeMatch(
    string Name,
    double CaloriesPerGram,
    double? ProteinPerGram,
    double? CarbsPerGram,
    double? FatPerGram,
    string Source);

/// <summary>
/// Resolves a numeric product barcode (EAN/UPC/GTIN) to an official food definition.
/// It is a database lookup, not an AI estimate — its own seam, separate from
/// <see cref="INutritionEstimator"/> (docs/barcode-lookup.md).
/// </summary>
public interface IBarcodeFoodLookup
{
    /// <summary>Whether the lookup is configured and should be offered to users.</summary>
    bool Enabled { get; }

    /// <summary>Resolve a barcode. Returns null when the product isn't found or
    /// nutrition data is missing. Implementations should be time-boxed (caller
    /// also wraps in a timeout).</summary>
    Task<BarcodeMatch?> LookupAsync(string barcode, CancellationToken ct);
}
