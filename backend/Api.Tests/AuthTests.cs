using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text;

namespace Api.Tests;

/// <summary>
/// Password-policy, registration and reset-flow tests for <see cref="AuthService"/> and
/// <see cref="AuthController"/>. Uses EF Core InMemory; no real network/crypto setup needed.
/// </summary>
public class AuthTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AuthService _service;
    private readonly AuthController _controller;
    private readonly CapturingEmailSender _email = new();

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
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PUBLIC_BASE_URL"] = "https://app.example.com" })
            .Build();
        _controller = new AuthController(_service, tokenService, _email, config,
            NullLogger<AuthController>.Instance)
        {
            // ResolveBaseUrl reads Request when PUBLIC_BASE_URL is unset; give it a context anyway.
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };
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

    // ── Password reset (token flow) ──

    [Fact]
    public async Task ResetPassword_WeakPassword_ReturnsPolicyMessage()
    {
        var result = await _controller.ResetPassword(new ResetPasswordRequest
        {
            Token = "whatever",
            NewPassword = "weakpass",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(AuthService.PasswordPolicyMessage, ErrorOf(bad));
    }

    [Fact]
    public async Task ResetPassword_UnknownToken_ReturnsBadRequest()
    {
        var result = await _controller.ResetPassword(new ResetPasswordRequest
        {
            Token = "not-a-real-token",
            NewPassword = "Str0ng!pw",
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RequestPasswordReset_UnknownEmail_IsGenericAndSendsNothing()
    {
        var result = await _controller.RequestPasswordReset(new RequestPasswordResetRequest
        {
            Email = "nobody@example.com",
        });

        Assert.IsType<OkObjectResult>(result); // generic response — no enumeration
        Assert.Empty(_email.Sent);
        Assert.Empty(_db.PasswordResetTokens);
    }

    [Fact]
    public async Task RequestPasswordReset_OidcOnlyAccount_SendsNothing()
    {
        // An SSO account has no local password; a reset must NOT mint one (that would
        // bypass CrimsonRaven). So no token, no email.
        _db.Users.Add(new User { Email = "sso@example.com", PasswordHash = null, ExternalSubject = "kc|123" });
        await _db.SaveChangesAsync();

        var result = await _controller.RequestPasswordReset(new RequestPasswordResetRequest
        {
            Email = "sso@example.com",
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(_email.Sent);
        Assert.Empty(_db.PasswordResetTokens);
    }

    [Fact]
    public async Task PasswordReset_FullFlow_ChangesPasswordAndTokenIsSingleUse()
    {
        await _service.RegisterAsync("reset@example.com", "Original1!");

        // Step 1: request a link — an email is sent and a token is stored (as a hash).
        var req = await _controller.RequestPasswordReset(new RequestPasswordResetRequest { Email = "reset@example.com" });
        Assert.IsType<OkObjectResult>(req);
        var sent = Assert.Single(_email.Sent);
        var rawToken = ExtractToken(sent.Text);
        Assert.NotEmpty(rawToken);
        Assert.DoesNotContain(rawToken, _db.PasswordResetTokens.Single().TokenHash); // stored hashed, not raw

        // Step 2: redeem it — the password changes.
        var reset = await _controller.ResetPassword(new ResetPasswordRequest { Token = rawToken, NewPassword = "Brandnew1!" });
        Assert.IsType<OkObjectResult>(reset);
        Assert.Null(await _service.LoginAsync("reset@example.com", "Original1!"));
        Assert.NotNull(await _service.LoginAsync("reset@example.com", "Brandnew1!"));

        // The token can't be replayed.
        var replay = await _controller.ResetPassword(new ResetPasswordRequest { Token = rawToken, NewPassword = "Another1!" });
        Assert.IsType<BadRequestObjectResult>(replay);
    }

    [Fact]
    public async Task ResetPassword_ExpiredToken_IsRejected()
    {
        var user = await _service.RegisterAsync("expired@example.com", "Original1!");
        var raw = "expired-raw-token";
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user!.Id,
            TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1), // already expired
        });
        await _db.SaveChangesAsync();

        var ok = await _service.ResetPasswordWithTokenAsync(raw, "Brandnew1!");
        Assert.False(ok);
    }

    [Fact]
    public async Task LoginAsync_AcceptsLegacyPbkdf2Hash()
    {
        // A password hashed with the pre-hardening format ([16-byte salt][20-byte hash],
        // PBKDF2-SHA256 @ 10k, base64) must still verify after the work-factor bump.
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2("Legacy1!pw", salt, 10000, HashAlgorithmName.SHA256, 20);
        var legacy = new byte[36];
        Array.Copy(salt, 0, legacy, 0, 16);
        Array.Copy(hash, 0, legacy, 16, 20);
        _db.Users.Add(new User { Email = "legacy@example.com", PasswordHash = Convert.ToBase64String(legacy) });
        await _db.SaveChangesAsync();

        Assert.NotNull(await _service.LoginAsync("legacy@example.com", "Legacy1!pw"));
        Assert.Null(await _service.LoginAsync("legacy@example.com", "WrongPass1!"));
    }

    private static string ExtractToken(string emailText)
    {
        var marker = "token=";
        var i = emailText.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        var start = i + marker.Length;
        var end = emailText.IndexOfAny([' ', '\n', '\r'], start);
        return Uri.UnescapeDataString(emailText[start..(end < 0 ? emailText.Length : end)]);
    }

    private static string? ErrorOf(BadRequestObjectResult bad) =>
        bad.Value?.GetType().GetProperty("error")?.GetValue(bad.Value) as string;

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<(string To, string Subject, string Html, string Text)> Sent { get; } = [];

        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody,
            CancellationToken ct = default)
        {
            Sent.Add((toEmail, subject, htmlBody, textBody));
            return Task.CompletedTask;
        }
    }
}
