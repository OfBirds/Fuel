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
    private static readonly HashSet<string> ValidActivityLevels =
        ["sedentary", "light", "moderate", "active", "very_active"];

    private static readonly HashSet<string> ValidMealPauseScopes =
        ["all", "non-snack"];

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
        if (!string.IsNullOrWhiteSpace(request.Sex))
        {
            if (!Enum.TryParse<Sex>(request.Sex, out var sex))
                return BadRequest(new { error = $"Invalid sex '{request.Sex}'." });
            user.Sex = sex;
        }
        if (!string.IsNullOrWhiteSpace(request.Constitution))
        {
            if (!Enum.TryParse<Constitution>(request.Constitution, out var con))
                return BadRequest(new { error = $"Invalid constitution '{request.Constitution}'." });
            user.Constitution = con;
        }
        if (request.YearOfBirth.HasValue) user.YearOfBirth = request.YearOfBirth;
        if (request.ActivityLevel is not null)
        {
            var al = request.ActivityLevel.Trim().ToLowerInvariant();
            if (!ValidActivityLevels.Contains(al))
                return BadRequest(new { error = $"Invalid activityLevel '{request.ActivityLevel}'. Valid values: {string.Join(", ", ValidActivityLevels)}." });
            user.ActivityLevel = al;
        }
        if (request.MealPauseHours.HasValue) user.MealPauseHours = request.MealPauseHours;
        if (request.MealPauseScope is not null)
        {
            var ms = request.MealPauseScope.Trim().ToLowerInvariant();
            if (!ValidMealPauseScopes.Contains(ms))
                return BadRequest(new { error = $"Invalid mealPauseScope '{request.MealPauseScope}'. Valid values: {string.Join(", ", ValidMealPauseScopes)}." });
            user.MealPauseScope = ms;
        }
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

    /// <summary>Check if a planned intake falls within the meal-pause window and whether
    /// its time is out of order relative to other same-day meals.</summary>
    [HttpGet("meal-pause-check")]
    public async Task<ActionResult<MealPauseCheckResponse>> CheckMealPause(
        Guid userId,
        [FromQuery] DateTime? intakeAtUtc,
        [FromQuery] string? mealType,
        [FromQuery] int tzOffsetMinutes,
        CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        var response = new MealPauseCheckResponse
        {
            IsWithinPause = false,
            MealPauseHours = user.MealPauseHours,
        };

        if (intakeAtUtc is null)
            return Ok(response);

        // ── Meal-order check (independent of pause config) ──
        var (orderMsg, orderAt) = GetMealOrderWarning(userId, mealType, intakeAtUtc.Value, tzOffsetMinutes);
        response.MealOrderWarning = orderMsg;
        response.MealOrderConflictAtUtc = orderAt;

        // ── Pause check ──
        if (user.MealPauseHours is null || user.MealPauseHours <= 0)
            return Ok(response);

        var scope = (user.MealPauseScope ?? "non-snack").Trim().ToLowerInvariant();
        var currentMealType = !string.IsNullOrWhiteSpace(mealType) ? mealType.Trim() : null;

        // Non-snack scope: don't warn at all when logging a snack.
        if (scope == "non-snack" && currentMealType is not null
            && string.Equals(currentMealType, "Snack", StringComparison.OrdinalIgnoreCase))
            return Ok(response);

        // Find the most recent entry before this intake, skipping same-meal-type
        // entries (second helpings never trigger a warning) and applying scope.
        IQueryable<FoodEntry> prevQuery = db.FoodEntries
            .Where(e => e.UserId == userId && e.IntakeAtUtc < intakeAtUtc.Value);

        if (currentMealType is not null)
            prevQuery = prevQuery.Where(e => e.MealType.ToString() != currentMealType);

        if (scope == "non-snack")
            prevQuery = prevQuery.Where(e => e.MealType != MealType.Snack);
        // "all" scope: snacks count for everyone — no extra filter.

        var previous = await prevQuery
            .OrderByDescending(e => e.IntakeAtUtc)
            .FirstOrDefaultAsync(ct);

        if (previous is null)
            return Ok(response);

        var hoursSince = (intakeAtUtc.Value - previous.IntakeAtUtc).TotalHours;
        response.HoursSinceLast = Math.Round(hoursSince, 1);
        response.LastFoodName = previous.FoodName;
        response.LastMealType = previous.MealType.ToString();
        response.IsWithinPause = hoursSince < user.MealPauseHours;

        return Ok(response);
    }

    // Returns the warning text (no time — the client appends the conflict time in the
    // viewer's local zone) and the conflicting entry's UTC instant.
    private (string? Message, DateTime? At) GetMealOrderWarning(
        Guid userId, string? mealType, DateTime intakeAtUtc, int tzOffsetMinutes)
    {
        if (string.IsNullOrWhiteSpace(mealType)
            || string.Equals(mealType.Trim(), "Snack", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var mt = mealType.Trim();
        // Group by the user's LOCAL calendar day, not the UTC date. Entries are stored in UTC and the
        // client passes its offset, so a late dinner and the next morning's breakfast — which can fall
        // on the same UTC date — aren't wrongly grouped across the user's midnight (the cause of the
        // "Breakfast is after yesterday's Dinner" false warning).
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes);
        var dayStart = (intakeAtUtc + offset).Date - offset;
        var dayEnd = dayStart.AddDays(1);

        var sameDay = db.FoodEntries
            .Where(e => e.UserId == userId
                && e.IntakeAtUtc >= dayStart && e.IntakeAtUtc < dayEnd
                && e.MealType != MealType.Snack
                && e.MealType.ToString() != mt)
            .Select(e => new { e.MealType, e.IntakeAtUtc })
            .ToList();

        if (sameDay.Count == 0) return (null, null);

        if (string.Equals(mt, "Breakfast", StringComparison.OrdinalIgnoreCase))
        {
            var earlier = sameDay
                .Where(e => e.MealType is MealType.Lunch or MealType.Dinner && e.IntakeAtUtc < intakeAtUtc)
                .OrderByDescending(e => e.IntakeAtUtc)
                .FirstOrDefault();
            if (earlier is not null)
                return ($"Breakfast is the first meal — {earlier.MealType} is logged earlier", earlier.IntakeAtUtc);
        }
        else if (string.Equals(mt, "Lunch", StringComparison.OrdinalIgnoreCase))
        {
            var dinnerBefore = sameDay
                .Where(e => e.MealType == MealType.Dinner && e.IntakeAtUtc < intakeAtUtc)
                .OrderByDescending(e => e.IntakeAtUtc).FirstOrDefault();
            if (dinnerBefore is not null)
                return ("Lunch is before Dinner — Dinner is logged earlier", dinnerBefore.IntakeAtUtc);

            var breakfastAfter = sameDay
                .Where(e => e.MealType == MealType.Breakfast && e.IntakeAtUtc > intakeAtUtc)
                .OrderBy(e => e.IntakeAtUtc).FirstOrDefault();
            if (breakfastAfter is not null)
                return ("Lunch is after Breakfast — Breakfast is logged later", breakfastAfter.IntakeAtUtc);
        }
        else if (string.Equals(mt, "Dinner", StringComparison.OrdinalIgnoreCase))
        {
            var later = sameDay
                .Where(e => e.MealType is MealType.Breakfast or MealType.Lunch && e.IntakeAtUtc > intakeAtUtc)
                .OrderBy(e => e.IntakeAtUtc)
                .FirstOrDefault();
            if (later is not null)
                return ($"Dinner is the last meal — {later.MealType} is logged later", later.IntakeAtUtc);
        }

        return (null, null);
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
