using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Api.Tests;

/// <summary>EF Core InMemory tests for the user-prefs endpoints.</summary>
public class UserControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly UserController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public UserControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefs_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        _controller = new UserController(_db);

        _db.Users.Add(new User
        {
            Id = _userId,
            Email = "test@example.com",
            PasswordHash = "hash",
            NotifyReleases = true
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetPrefs_ReturnsCurrentValues()
    {
        var result = await _controller.GetPrefs(_userId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prefs = Assert.IsType<UserPrefsResponse>(ok.Value);
        Assert.True(prefs.NotifyReleases);
    }

    [Fact]
    public async Task GetPrefs_UnknownUser_NotFound()
    {
        var result = await _controller.GetPrefs(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePrefs_PersistsAndEchoesNewValue()
    {
        var result = await _controller.UpdatePrefs(
            _userId, new UpdateUserPrefsRequest { NotifyReleases = false }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prefs = Assert.IsType<UserPrefsResponse>(ok.Value);
        Assert.False(prefs.NotifyReleases);

        // Persisted to the DB, not just echoed.
        var stored = await _db.Users.FindAsync(_userId);
        Assert.False(stored!.NotifyReleases);
    }

    [Fact]
    public async Task UpdatePrefs_UnknownUser_NotFound()
    {
        var result = await _controller.UpdatePrefs(
            Guid.NewGuid(), new UpdateUserPrefsRequest { NotifyReleases = false }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
