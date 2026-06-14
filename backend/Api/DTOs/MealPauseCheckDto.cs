namespace Api.DTOs;

public class MealPauseCheckResponse
{
    public bool IsWithinPause { get; set; }
    public double? HoursSinceLast { get; set; }
    public double? MealPauseHours { get; set; }
}
