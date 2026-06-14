using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

public class WeightControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly WeightController _controller;
    private readonly Guid _userId;

    public WeightControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"weight_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        _controller = new WeightController(_db);

        var user = new User { Email = "test@example.com", PasswordHash = "hash" };
        _db.Users.Add(user);
        _db.SaveChanges();
        _userId = user.Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetWeights_Empty_ReturnsEmptyList()
    {
        var result = await _controller.GetWeights(_userId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WeightEntryResponse>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetWeights_ReturnsNewestFirst()
    {
        _db.WeightEntries.AddRange(
            new WeightEntry { UserId = _userId, Weight = 80, RecordedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new WeightEntry { UserId = _userId, Weight = 82, RecordedAtUtc = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc) }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetWeights(_userId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WeightEntryResponse>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.Equal(82, list[0].Weight); // newest first
    }

    [Fact]
    public async Task GetWeights_ComputesDeltaPercent()
    {
        _db.WeightEntries.AddRange(
            new WeightEntry { UserId = _userId, Weight = 80, RecordedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new WeightEntry { UserId = _userId, Weight = 82, RecordedAtUtc = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc) },
            new WeightEntry { UserId = _userId, Weight = 79, RecordedAtUtc = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc) }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.GetWeights(_userId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WeightEntryResponse>>(ok.Value);
        Assert.Equal(3, list.Count);

        // Newest (79): delta vs 82 = (79-82)/82*100 = -3.66...%
        Assert.NotNull(list[0].DeltaPercent);
        Assert.True(list[0].DeltaPercent < 0);

        // Middle (82): delta vs 80 = (82-80)/80*100 = 2.5%
        Assert.NotNull(list[1].DeltaPercent);
        Assert.True(list[1].DeltaPercent > 0);

        // Oldest (80): no previous entry
        Assert.Null(list[2].DeltaPercent);
    }

    [Fact]
    public async Task GetWeights_SingleEntry_DeltaNull()
    {
        _db.WeightEntries.Add(new WeightEntry { UserId = _userId, Weight = 80 });
        await _db.SaveChangesAsync();

        var result = await _controller.GetWeights(_userId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WeightEntryResponse>>(ok.Value);
        Assert.Single(list);
        Assert.Null(list[0].DeltaPercent);
    }

    [Fact]
    public async Task CreateWeight_CreatesAndReturns()
    {
        var request = new CreateWeightEntryRequest { Weight = 85, RecordedAtUtc = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc) };
        var result = await _controller.CreateWeight(_userId, request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WeightEntryResponse>(created.Value);
        Assert.Equal(85, response.Weight);
        Assert.Null(response.DeltaPercent); // single entry

        Assert.Single(_db.WeightEntries);
    }

    [Fact]
    public async Task CreateWeight_DefaultDate_DefaultsToUtcNow()
    {
        var request = new CreateWeightEntryRequest { Weight = 75 };
        var result = await _controller.CreateWeight(_userId, request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WeightEntryResponse>(created.Value);
        Assert.True(response.RecordedAtUtc <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task CreateWeight_UnknownUser_ReturnsNotFound()
    {
        var request = new CreateWeightEntryRequest { Weight = 70 };
        var result = await _controller.CreateWeight(Guid.NewGuid(), request, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteWeight_RemovesAndReturnsNoContent()
    {
        var entry = new WeightEntry { UserId = _userId, Weight = 80 };
        _db.WeightEntries.Add(entry);
        await _db.SaveChangesAsync();

        var result = await _controller.DeleteWeight(_userId, entry.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(result);
        Assert.Empty(_db.WeightEntries);
    }

    [Fact]
    public async Task DeleteWeight_NotFound_ReturnsNotFound()
    {
        var result = await _controller.DeleteWeight(_userId, Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteWeight_WrongUser_ReturnsNotFound()
    {
        var entry = new WeightEntry { UserId = _userId, Weight = 80 };
        _db.WeightEntries.Add(entry);
        await _db.SaveChangesAsync();

        var result = await _controller.DeleteWeight(Guid.NewGuid(), entry.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }
}
