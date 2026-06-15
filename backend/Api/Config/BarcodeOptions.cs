namespace Api.Config;

/// <summary>Flat-env config for barcode/EAN lookup (docs/barcode-lookup.md §Configuration).</summary>
public class BarcodeOptions
{
    /// <summary>Master kill-switch: when false the scan affordance is hidden and
    /// the controller returns "not configured".</summary>
    public bool Enabled { get; set; }

    /// <summary>Open Food Facts endpoint. Defaults to the public world URL.</summary>
    public string BaseUrl { get; set; } = "https://world.openfoodfacts.org";

    /// <summary>Call timeout in seconds. Default 10.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
