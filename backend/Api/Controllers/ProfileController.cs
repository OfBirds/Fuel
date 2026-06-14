using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/user/{userId}")]
public class ProfileController(AppDbContext db, IProfileService profileService) : ControllerBase
{
    /// <summary>Get profile fields. Returns nulls if onboarding hasn't been completed.</summary>
    [HttpGet("profile")]
    public async Task<ActionResult<ProfileResponse>> GetProfile(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();
        return Ok(ToResponse(user));
    }

    /// <summary>Update profile fields. All fields optional (partial update).</summary>
    [HttpPut("profile")]
    public async Task<ActionResult<ProfileResponse>> UpdateProfile(
        Guid userId, [FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        if (request.Height.HasValue) user.Height = request.Height;
        if (request.Sex is not null && Enum.TryParse<Sex>(request.Sex, out var sex)) user.Sex = sex;
        if (request.Constitution is not null && Enum.TryParse<Constitution>(request.Constitution, out var con)) user.Constitution = con;
        if (request.YearOfBirth.HasValue) user.YearOfBirth = request.YearOfBirth;
        if (request.ActivityLevel is not null) user.ActivityLevel = request.ActivityLevel;
        if (request.MealPauseHours.HasValue) user.MealPauseHours = request.MealPauseHours;
        if (request.MealPauseScope is not null) user.MealPauseScope = request.MealPauseScope;
        if (request.ShowMacros.HasValue) user.ShowMacros = request.ShowMacros.Value;

        await db.SaveChangesAsync(ct);
        return Ok(ToResponse(user));
    }

    /// <summary>Compute metabolism values from the latest weight and profile data.</summary>
    [HttpGet("metabolism")]
    public async Task<ActionResult<MetabolismResponse>> GetMetabolism(
        Guid userId,
        [FromQuery] double? weightKg,
        [FromQuery] string? activityLevel,
        CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        if (user.Height is null || user.Height == 0)
            return BadRequest(new { error = "Profile not set up (height missing)." });

        // Use provided weight or fetch latest weigh-in
        double w;
        if (weightKg.HasValue && weightKg.Value > 0)
        {
            w = weightKg.Value;
        }
        else
        {
            var latestWeight = await db.WeightEntries
                .Where(we => we.UserId == userId)
                .OrderByDescending(we => we.RecordedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (latestWeight is null)
                return BadRequest(new { error = "No weight entries. Add a weigh-in first." });

            w = latestWeight.Weight;
        }

        if (user.YearOfBirth is null)
            return BadRequest(new { error = "Year of birth not set." });

        if (user.Sex is null)
            return BadRequest(new { error = "Sex not set." });

        var age = profileService.CalculateAge(user.YearOfBirth.Value);
        var sex = user.Sex.Value;
        var level = activityLevel ?? user.ActivityLevel ?? "sedentary";

        var bmr = profileService.CalculateBmr(w, user.Height.Value, age, sex);
        var tdee = profileService.CalculateTdee(bmr, level);
        var bmi = profileService.CalculateBmi(w, user.Height.Value);

        var response = new MetabolismResponse
        {
            Bmr = Math.Round(bmr, 1),
            Tdee = Math.Round(tdee, 1),
            Bmi = Math.Round(bmi, 1),
            ActivityLevel = level,
        };

        if (user.Constitution.HasValue)
        {
            var (min, max) = profileService.CalculateIdealWeightRange(user.Height.Value, user.Constitution.Value);
            response.IdealWeightMin = Math.Round(min, 1);
            response.IdealWeightMax = Math.Round(max, 1);
        }

        return Ok(response);
    }

    /// <summary>Check if a planned intake falls within the meal-pause window.</summary>
    [HttpGet("meal-pause-check")]
    public async Task<ActionResult<MealPauseCheckResponse>> CheckMealPause(
        Guid userId,
        [FromQuery] DateTime? intakeAtUtc,
        [FromQuery] string? mealType,
        CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        var response = new MealPauseCheckResponse
        {
            IsWithinPause = false,
            MealPauseHours = user.MealPauseHours,
        };

        if (user.MealPauseHours is null || user.MealPauseHours <= 0 || intakeAtUtc is null)
            return Ok(response);

        var scope = (user.MealPauseScope ?? "all").Trim().ToLowerInvariant();

        // Find the most recent entry before this intake
        var prevQuery = db.FoodEntries
            .Where(e => e.UserId == userId && e.IntakeAtUtc < intakeAtUtc.Value);

        if (scope == "non-snack")
            prevQuery = prevQuery.Where(e => e.MealType != MealType.Snack);
        // "all" scope means all entries — no additional filter needed

        var previous = await prevQuery
            .OrderByDescending(e => e.IntakeAtUtc)
            .FirstOrDefaultAsync(ct);

        if (previous is null)
            return Ok(response);

        var hoursSince = (intakeAtUtc.Value - previous.IntakeAtUtc).TotalHours;
        response.HoursSinceLast = Math.Round(hoursSince, 1);
        response.IsWithinPause = hoursSince < user.MealPauseHours;

        return Ok(response);
    }

    private static ProfileResponse ToResponse(User u) => new()
    {
        Height = u.Height,
        Sex = u.Sex?.ToString(),
        Constitution = u.Constitution?.ToString(),
        YearOfBirth = u.YearOfBirth,
        ActivityLevel = u.ActivityLevel,
        MealPauseHours = u.MealPauseHours,
        MealPauseScope = u.MealPauseScope,
        ShowMacros = u.ShowMacros,
    };
}
