namespace Api.DTOs;

public class MealPauseCheckResponse
{
    public bool IsWithinPause { get; set; }
    public double? HoursSinceLast { get; set; }
    public string? LastFoodName { get; set; }
    public string? LastMealType { get; set; }
    public double? MealPauseHours { get; set; }
    /// <summary>Null unless the meal time is out of order relative to same-day
    /// entries (e.g. Dinner before Lunch). Snacks never trigger this. The message
    /// has no time in it — the client appends the conflict time below, formatted in
    /// the viewer's local zone (the server can't know it).</summary>
    public string? MealOrderWarning { get; set; }
    /// <summary>UTC instant of the conflicting entry, for the client to format and
    /// append to <see cref="MealOrderWarning"/>. Null when there's no conflict.</summary>
    public DateTime? MealOrderConflictAtUtc { get; set; }
}
