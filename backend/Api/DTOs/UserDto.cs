namespace Api.DTOs;

/// <summary>
/// User-facing preferences served to / accepted from the settings page. Starts
/// with the release-email opt-in; grows as features add prefs.
/// </summary>
public class UserPrefsResponse
{
    public bool NotifyReleases { get; set; }
}

public class UpdateUserPrefsRequest
{
    public bool NotifyReleases { get; set; }
}
