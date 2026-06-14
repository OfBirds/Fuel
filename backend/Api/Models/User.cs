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

    /// <summary>Daily calorie goal. Defaults to 2100; user can change or clear it.</summary>
    public double? DailyCalorieGoal { get; set; } = 2100;

    // ── Profile (null = onboarding not completed) ──
    public double? Height { get; set; }
    public Sex? Sex { get; set; }
    public Constitution? Constitution { get; set; }
    public int? YearOfBirth { get; set; }
    public string? ActivityLevel { get; set; }

    // ── Meal pause ──
    /// <summary>Hours to wait between meals. 0 (the default) or negative turns the feature off.</summary>
    public double? MealPauseHours { get; set; } = 0;
    public string? MealPauseScope { get; set; }

    // ── Display ──
    public bool ShowMacros { get; set; }
}
