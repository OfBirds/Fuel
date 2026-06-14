using Api.Controllers;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Api.IntegrationTests;

/// <summary>
/// Real-Postgres integration tests for <see cref="EntryController"/>.
/// </summary>
[Collection("Postgres")]
public class EntryIntegrationTests(PostgresFixture db)
{
    [Fact]
    public async Task GetEntries_TimestamptzRange_RoundTrips()
    {
        // ── Arrange ────────────────────────────────────────────────
        var userId = Guid.NewGuid();
        var ctx = db.CreateContext();

        // The bug InMemory missed: DateTimeKind.Unspecified was accepted by InMemory
        // but Npgsql rejects it against a timestamptz column.  We store Utc values
        // and query with an Unspecified range — real Npgsql must survive the
        // round-trip and return Utc values.
        var inWindowUtc = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);
        var outOfWindowUtc = new DateTime(2026, 6, 14, 23, 0, 0, DateTimeKind.Utc);

        ctx.Users.Add(new User
        {
            Id = userId,
            Email = "entry-test@example.com",
            PasswordHash = "hash",
        });

        ctx.FoodEntries.AddRange(
            new FoodEntry { UserId = userId, FoodId = null, FoodName = "in-window",  UoM = "g", Calories = 100, Quantity = 1, IntakeAtUtc = inWindowUtc },
            new FoodEntry { UserId = userId, FoodId = null, FoodName = "out-window", UoM = "g", Calories = 200, Quantity = 1, IntakeAtUtc = outOfWindowUtc }
        );
        await ctx.SaveChangesAsync();

        // ── Act ────────────────────────────────────────────────────
        var controller = new EntryController(ctx);
        var from = new DateTime(2026, 6, 14, 0,  0, 0, DateTimeKind.Unspecified);
        var to   = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Unspecified);

        // NOTE: query-string binding would parse the trailing 'Z' to local time,
        // so the controller calls .ToUniversalTime().  Simulate that here:
        var result = await controller.GetEntries(userId, from, to, date: null, CancellationToken.None);

        // ── Assert ─────────────────────────────────────────────────
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Single(list);                    // only the in-window entry
        Assert.Equal("in-window", list[0].FoodName);

        // The returned DateTime must be DateTimeKind.Utc — real Npgsql
        // stores/reads timestamptz as Utc.  InMemory would leave it Unspecified.
        Assert.Equal(DateTimeKind.Utc, list[0].IntakeAtUtc.Kind);
    }

    [Fact]
    public async Task GetEntries_MultipleInWindow_ReturnsAllOrdered()
    {
        var userId = Guid.NewGuid();
        var ctx = db.CreateContext();
        ctx.Users.Add(new User
        {
            Id = userId,
            Email = "multi@example.com",
            PasswordHash = "hash",
        });

        var baseTime = new DateTime(2026, 6, 14, 10, 0, 0, DateTimeKind.Utc);
        ctx.FoodEntries.AddRange(
            new FoodEntry { UserId = userId, FoodId = null, FoodName = "first",  UoM = "g", Calories = 100, Quantity = 1, IntakeAtUtc = baseTime.AddMinutes(10) },
            new FoodEntry { UserId = userId, FoodId = null, FoodName = "second", UoM = "g", Calories = 200, Quantity = 1, IntakeAtUtc = baseTime.AddMinutes(5) },  // earlier
            new FoodEntry { UserId = userId, FoodId = null, FoodName = "third",  UoM = "g", Calories = 300, Quantity = 1, IntakeAtUtc = baseTime.AddMinutes(20) }
        );
        await ctx.SaveChangesAsync();

        var controller = new EntryController(ctx);
        var result = await controller.GetEntries(userId,
            from: baseTime, to: baseTime.AddHours(1), date: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<EntryResponse>>(ok.Value);
        Assert.Equal(3, list.Count);
        Assert.Equal("second", list[0].FoodName); // earliest first
        Assert.Equal("first",  list[1].FoodName);
        Assert.Equal("third",  list[2].FoodName);
    }
}
