using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        if (!await authService.ValidatePasswordAsync(request.Password))
            return BadRequest(new { error = AuthService.PasswordPolicyMessage });

        var user = await authService.RegisterAsync(request.Email, request.Password);
        if (user == null)
            return BadRequest(new { error = "An account with that email already exists." });

        var token = GenerateSimpleToken(user.Id);
        return Ok(new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Token = token,
            NotifyReleases = user.NotifyReleases
        });
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        var user = await authService.LoginAsync(request.Email, request.Password);
        if (user == null)
            return Unauthorized(new { error = "Invalid email or password" });

        var token = GenerateSimpleToken(user.Id);
        return Ok(new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Token = token,
            NotifyReleases = user.NotifyReleases
        });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        if (!await authService.ValidatePasswordAsync(request.NewPassword))
            return BadRequest(new { error = AuthService.PasswordPolicyMessage });

        var success = await authService.ResetPasswordAsync(request.Email, request.NewPassword);
        if (!success)
            return BadRequest(new { error = "No account found with that email." });

        return Ok(new { message = "Password reset successful" });
    }

    // TODO(SECURITY): demo-only token. It is Base64(userId:ticks) — unsigned,
    // unverified, and there is NO [Authorize]/validation middleware anywhere, so
    // the API is effectively open. Replace with real auth (e.g.
    // AddAuthentication().AddJwtBearer() + a signed JWT with an exp claim, and
    // [Authorize] on protected controllers) BEFORE any real/public deployment.
    // See README "Before you ship". Password hashing (PBKDF2) is fine as-is.
    private static string GenerateSimpleToken(Guid userId)
    {
        var tokenData = $"{userId}:{DateTime.UtcNow.Ticks}";
        var tokenBytes = System.Text.Encoding.UTF8.GetBytes(tokenData);
        return Convert.ToBase64String(tokenBytes);
    }
}
