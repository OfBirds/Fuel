using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

public class ProfileControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ProfileController _controller;
    private readonly Guid _userId;

    public ProfileControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"profile_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        var profileService = new ProfileService();
        _controller = new ProfileController(_db, profileService);

        var user = new User { Email = "test@example.com", PasswordHash = "hash" };
        _db.Users.Add(user);
        _db.SaveChanges();
        _userId = user.Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetProfile_NoProfile_ReturnsDefaults()
    {
        var result = await _controller.GetProfile(_userId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var profile = Assert.IsType<ProfileResponse>(ok.Value);
        Assert.Null(profile.Height);
        Assert.False(profile.ShowMacros);
    }

    [Fact]
    public async Task UpdateProfile_InvalidSex_ReturnsBadRequest()
    {
        var result = await _controller.UpdateProfile(
            _userId, new UpdateProfileRequest { Sex = "Helicopter" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);

        // Not silently persisted.
        var user = await _db.Users.FindAsync(_userId);
        Assert.Null(user!.Sex);
    }

    [Fact]
    public async Task GetProfile_UnknownUser_ReturnsNotFound()
    {
        var result = await _controller.GetProfile(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateProfile_PersistsAndEchoes()
    {
        var request = new UpdateProfileRequest
        {
            Height = 180, Sex = "Male", Constitution = "Medium",
            YearOfBirth = 1996, ActivityLevel = "moderate",
        };

        var result = await _controller.UpdateProfile(_userId, request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var profile = Assert.IsType<ProfileResponse>(ok.Value);
        Assert.Equal(180, profile.Height);
        Assert.Equal("Male", profile.Sex);
        Assert.Equal("Medium", profile.Constitution);
        Assert.Equal(1996, profile.YearOfBirth);

        // Persisted
        var user = await _db.Users.FindAsync(_userId);
        Assert.Equal(180, user!.Height);
        Assert.Equal(Sex.Male, user.Sex);
    }

    [Fact]
    public async Task UpdateProfile_PartialUpdate_KeepsExisting()
    {
        // Set initial profile
        var user = await _db.Users.FindAsync(_userId);
        user!.Height = 180;
        user.Sex = Sex.Male;
        user.ShowMacros = true;
        await _db.SaveChangesAsync();

        // Partial update — only change showMacros
        var request = new UpdateProfileRequest { ShowMacros = false };

        var result = await _controller.UpdateProfile(_userId, request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var profile = Assert.IsType<ProfileResponse>(ok.Value);

        Assert.Equal(180, profile.Height); // unchanged
        Assert.Equal("Male", profile.Sex); // unchanged
        Assert.False(profile.ShowMacros);  // updated
    }

    [Fact]
    public async Task GetMetabolism_ReturnsComputedValues()
    {
        // Seed weight
        _db.WeightEntries.Add(new WeightEntry { UserId = _userId, Weight = 80, RecordedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        // Set profile
        var user = await _db.Users.FindAsync(_userId);
        user!.Height = 180;
        user.YearOfBirth = 1996;
        user.Sex = Sex.Male;
        user.ActivityLevel = "moderate";
        await _db.SaveChangesAsync();

        var result = await _controller.GetMetabolism(_userId, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var m = Assert.IsType<MetabolismResponse>(ok.Value);
        Assert.True(m.Bmr > 0);
        Assert.True(m.Tdee > m.Bmr);
        Assert.True(m.Bmi > 0);
    }

    [Fact]
    public async Task GetMetabolism_NoWeight_ReturnsBadRequest()
    {
        var user = await _db.Users.FindAsync(_userId);
        user!.Height = 180;
        user.YearOfBirth = 1996;
        await _db.SaveChangesAsync();

        var result = await _controller.GetMetabolism(_userId, null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMetabolism_SexNotSet_ReturnsBadRequest()
    {
        // Has height, year of birth and a weigh-in, but no sex → must not silently
        // assume a sex (it changes BMR by ~166 kcal).
        _db.WeightEntries.Add(new WeightEntry { UserId = _userId, Weight = 70, RecordedAtUtc = DateTime.UtcNow });
        var user = await _db.Users.FindAsync(_userId);
        user!.Height = 170;
        user.YearOfBirth = 1990;
        // user.Sex intentionally left null
        await _db.SaveChangesAsync();

        var result = await _controller.GetMetabolism(_userId, null, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task MealPauseCheck_WithinWindow_ReturnsWarning()
    {
        var now = DateTime.UtcNow;
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Test", MealType = MealType.Lunch,
            Quantity = 100, UoM = "g", Calories = 100,
            IntakeAtUtc = now.AddHours(-2),
        });
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(_userId);
        user!.MealPauseHours = 4;
        user.MealPauseScope = "all";
        await _db.SaveChangesAsync();

        var result = await _controller.CheckMealPause(
            _userId, now, "Dinner", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.True(check.IsWithinPause);
        Assert.NotNull(check.HoursSinceLast);
    }

    [Fact]
    public async Task MealPauseCheck_OutsideWindow_ReturnsNoWarning()
    {
        var now = DateTime.UtcNow;
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Test", MealType = MealType.Lunch,
            Quantity = 100, UoM = "g", Calories = 100,
            IntakeAtUtc = now.AddHours(-6),
        });
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(_userId);
        user!.MealPauseHours = 4;
        user.MealPauseScope = "all";
        await _db.SaveChangesAsync();

        var result = await _controller.CheckMealPause(
            _userId, now, "Dinner", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.False(check.IsWithinPause);
    }

    [Fact]
    public async Task MealPauseCheck_NoPauseConfigured_ReturnsNoWarning()
    {
        var result = await _controller.CheckMealPause(
            _userId, DateTime.UtcNow, "Lunch", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.False(check.IsWithinPause);
    }

    [Fact]
    public async Task MealPauseCheck_NonSnackScope_IgnoresSnackEntries()
    {
        var now = DateTime.UtcNow;
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Apple", MealType = MealType.Snack,
            Quantity = 1, UoM = "piece", Calories = 80,
            IntakeAtUtc = now.AddHours(-1),
        });
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(_userId);
        user!.MealPauseHours = 4;
        user.MealPauseScope = "non-snack";
        await _db.SaveChangesAsync();

        var result = await _controller.CheckMealPause(
            _userId, now, "Lunch", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        // Snack is ignored, no previous in-scope intake → no warning
        Assert.False(check.IsWithinPause);
    }

    [Fact]
    public async Task MealPauseCheck_SameMealType_IsSkipped()
    {
        var now = DateTime.UtcNow;
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Chicken", MealType = MealType.Lunch,
            Quantity = 100, UoM = "g", Calories = 200,
            IntakeAtUtc = now.AddHours(-0.5),
        });
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(_userId);
        user!.MealPauseHours = 4;
        user.MealPauseScope = "all";
        await _db.SaveChangesAsync();

        // Second Lunch, 30 min later — should NOT warn (same meal, second helping)
        var result = await _controller.CheckMealPause(
            _userId, now, "Lunch", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.False(check.IsWithinPause);
    }

    [Fact]
    public async Task MealPauseCheck_ReturnsLastFoodName()
    {
        var now = DateTime.UtcNow;
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Pizza", MealType = MealType.Lunch,
            Quantity = 200, UoM = "g", Calories = 500,
            IntakeAtUtc = now.AddHours(-1),
        });
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(_userId);
        user!.MealPauseHours = 4;
        user.MealPauseScope = "all";
        await _db.SaveChangesAsync();

        // Dinner 1h after Lunch → within pause → returns "Pizza"
        var result = await _controller.CheckMealPause(
            _userId, now, "Dinner", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.True(check.IsWithinPause);
        Assert.Equal("Pizza", check.LastFoodName);
        Assert.Equal("Lunch", check.LastMealType);
    }

    [Fact]
    public async Task MealPauseCheck_NonSnackScope_SnackIsNotChecked()
    {
        var now = DateTime.UtcNow;
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Steak", MealType = MealType.Dinner,
            Quantity = 200, UoM = "g", Calories = 500,
            IntakeAtUtc = now.AddHours(-0.5),
        });
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(_userId);
        user!.MealPauseHours = 4;
        user.MealPauseScope = "non-snack";
        await _db.SaveChangesAsync();

        // Snack logged 30 min after Dinner — non-snack scope means snacks are
        // never checked, so no warning even though it's within the pause window.
        var result = await _controller.CheckMealPause(
            _userId, now, "Snack", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.False(check.IsWithinPause);
    }

    [Fact]
    public async Task MealPauseCheck_MealOrderWarning_DinnerBeforeLunch()
    {
        var now = DateTime.UtcNow;
        // Lunch already logged at 13:00
        var lunchTime = now.Date.AddHours(13);
        _db.FoodEntries.Add(new FoodEntry
        {
            UserId = _userId, FoodName = "Salad", MealType = MealType.Lunch,
            Quantity = 100, UoM = "g", Calories = 200,
            IntakeAtUtc = lunchTime,
        });
        await _db.SaveChangesAsync();

        // Logging Dinner at 10:00 — before Lunch — should trigger order warning.
        var dinnerTime = now.Date.AddHours(10);
        var result = await _controller.CheckMealPause(
            _userId, dinnerTime, "Dinner", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var check = Assert.IsType<MealPauseCheckResponse>(ok.Value);
        Assert.NotNull(check.MealOrderWarning);
        Assert.Contains("Dinner is the last meal", check.MealOrderWarning);
    }
}
