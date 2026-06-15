using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Api.Tests;

/// <summary>
/// Password-policy + registration tests for <see cref="AuthService"/> and
/// <see cref="AuthController"/>. Uses EF Core InMemory; no real network/crypto setup needed.
/// </summary>
public class AuthTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AuthService _service;
    private readonly AuthController _controller;

    public AuthTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"auth_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        _service = new AuthService(_db);
        var tokenService = new JwtTokenService(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-at-least-32-bytes!!")),
            TimeSpan.FromDays(30));
        _controller = new AuthController(_service, tokenService);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("Str0ng!pw", true)]      // 8+, letter, digit, special
    [InlineData("aB3$aaaa", true)]
    [InlineData("short1!", false)]       // < 8
    [InlineData("nodigits!", false)]     // no digit
    [InlineData("12345678!", false)]     // no letter
    [InlineData("noSpecial1", false)]    // no special char
    [InlineData("", false)]
    public void IsPasswordValid_EnforcesPolicy(string password, bool expected)
    {
        Assert.Equal(expected, AuthService.IsPasswordValid(password));
    }

    [Fact]
    public async Task RegisterAsync_RejectsWeakPassword()
    {
        var user = await _service.RegisterAsync("weak@example.com", "password"); // no digit/special
        Assert.Null(user);
        Assert.Empty(_db.Users);
    }

    [Fact]
    public async Task Register_WeakPassword_ReturnsPolicyMessage()
    {
        var result = await _controller.Register(new RegisterRequest
        {
            Email = "weak@example.com",
            Password = "password",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(AuthService.PasswordPolicyMessage, ErrorOf(bad));
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsSpecificMessage()
    {
        var first = await _controller.Register(new RegisterRequest
        {
            Email = "dupe@example.com",
            Password = "Str0ng!pw",
        });
        Assert.IsType<OkObjectResult>(first.Result);

        var second = await _controller.Register(new RegisterRequest
        {
            Email = "dupe@example.com",
            Password = "An0ther!pw",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(second.Result);
        Assert.Equal("An account with that email already exists.", ErrorOf(bad));
    }

    [Fact]
    public async Task Register_ValidPassword_CreatesUser()
    {
        var result = await _controller.Register(new RegisterRequest
        {
            Email = "good@example.com",
            Password = "Str0ng!pw",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("good@example.com", auth.Email);
        Assert.Single(_db.Users);
    }

    [Fact]
    public async Task ResetPassword_WeakPassword_ReturnsPolicyMessage()
    {
        var result = await _controller.ResetPassword(new ResetPasswordRequest
        {
            Email = "whoever@example.com",
            NewPassword = "weakpass",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(AuthService.PasswordPolicyMessage, ErrorOf(bad));
    }

    private static string? ErrorOf(BadRequestObjectResult bad) =>
        bad.Value?.GetType().GetProperty("error")?.GetValue(bad.Value) as string;
}
