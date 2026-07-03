namespace Api.DTOs;

/// <summary>Response from <c>GET /api/barcode/lookup/{code}</c>.</summary>
public class BarcodeLookupResponse
{
    /// <summary>True when the product was found in OFF (or our cache). False = unknown product.</summary>
    public bool Found { get; set; }

    /// <summary>True when the catalogue food was just created from OFF data (not a prior cache hit).</summary>
    public bool IsNew { get; set; }

    /// <summary>The resolved catalogue food (present when <see cref="Found"/> is true).</summary>
    public FoodResponse? Food { get; set; }

    /// <summary>Human-readable message when not found or disabled.</summary>
    public string? Message { get; set; }
}
