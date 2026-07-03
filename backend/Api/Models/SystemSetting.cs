namespace Api.Models;

/// <summary>
/// Simple key/value store for small bits of app-wide state (e.g. the last app
/// version that release-notification emails were sent for).
/// </summary>
public class SystemSetting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}
