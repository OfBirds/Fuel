namespace Api.DTOs;

/// <summary>
/// User-facing preferences served to / accepted from the settings page. Starts
/// with the release-email opt-in; grows as features add prefs.
/// </summary>
public class UserPrefsResponse
{
    public bool NotifyReleases { get; set; }
    public double? DailyCalorieGoal { get; set; }

    /// <summary>
    /// Mirror of the profile macro-display toggle, served here so the lightweight
    /// pages that already read /prefs can gate macro UI without a second profile fetch.
    /// Read-only on this endpoint — it's set via the profile endpoint.
    /// </summary>
    public bool ShowMacros { get; set; }
}

public class UpdateUserPrefsRequest
{
    public bool NotifyReleases { get; set; }
    public double? DailyCalorieGoal { get; set; }
}
