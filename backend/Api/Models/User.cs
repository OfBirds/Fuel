namespace Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this user receives "new version released" emails.</summary>
    public bool NotifyReleases { get; set; } = true;

    /// <summary>Opaque token for one-click unsubscribe links.</summary>
    public Guid UnsubscribeToken { get; set; } = Guid.NewGuid();

    /// <summary>Daily calorie goal, set by the user. Null if not configured.</summary>
    public double? DailyCalorieGoal { get; set; }

    // ── Profile (null = onboarding not completed) ──
    public double? Height { get; set; }
    public Sex? Sex { get; set; }
    public Constitution? Constitution { get; set; }
    public int? YearOfBirth { get; set; }
    public string? ActivityLevel { get; set; }

    // ── Meal pause ──
    public double? MealPauseHours { get; set; }
    public string? MealPauseScope { get; set; }

    // ── Display ──
    public bool ShowMacros { get; set; }
}
