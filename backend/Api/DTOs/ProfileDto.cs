namespace Api.DTOs;

public class ProfileResponse
{
    public double? Height { get; set; }
    public string? Sex { get; set; }
    public string? Constitution { get; set; }
    public int? YearOfBirth { get; set; }
    public string? ActivityLevel { get; set; }
    public double? MealPauseHours { get; set; }
    public string? MealPauseScope { get; set; }
    public bool ShowMacros { get; set; }
}

public class UpdateProfileRequest
{
    public double? Height { get; set; }
    public string? Sex { get; set; }
    public string? Constitution { get; set; }
    public int? YearOfBirth { get; set; }
    public string? ActivityLevel { get; set; }
    public double? MealPauseHours { get; set; }
    public string? MealPauseScope { get; set; }
    public bool? ShowMacros { get; set; }
}
