using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous] // login/register/reset must work before a token exists
public class AuthController(
    IAuthService authService,
    ITokenService tokenService,
    IEmailSender emailSender,
    IConfiguration config,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        if (!await authService.ValidatePasswordAsync(request.Password))
            return BadRequest(new { error = AuthService.PasswordPolicyMessage });

        var user = await authService.RegisterAsync(request.Email, request.Password);
        if (user == null)
            return BadRequest(new { error = "An account with that email already exists." });

        var token = tokenService.CreateToken(user.Id, user.Email);
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

        var token = tokenService.CreateToken(user.Id, user.Email);
        return Ok(new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Token = token,
            NotifyReleases = user.NotifyReleases
        });
    }

    /// <summary>
    /// Step 1 of the local password reset: email a single-use reset link. Always returns
    /// the same generic response whether or not the address has an account, so it can't be
    /// used to enumerate users. Only local accounts get a link — OIDC-only accounts reset
    /// through CrimsonRaven.
    /// </summary>
    [HttpPost("request-password-reset")]
    public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetRequest request)
    {
        var rawToken = await authService.CreatePasswordResetTokenAsync(request.Email);
        if (rawToken != null)
        {
            var baseUrl = ResolveBaseUrl();
            var link = $"{baseUrl}/#reset?token={Uri.EscapeDataString(rawToken)}";
            var (subject, html, text) = BuildResetEmail(link);
            try
            {
                await emailSender.SendAsync(request.Email, subject, html, text);
            }
            catch (Exception ex)
            {
                // Don't leak delivery failures to the caller; a misconfigured SMTP shouldn't
                // reveal that the address exists. Log for the operator.
                logger.LogError(ex, "Failed to send password reset email.");
            }
        }

        return Ok(new { message = "If an account with that email exists, a reset link has been sent." });
    }

    /// <summary>Step 2: redeem the emailed token and set a new password.</summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        if (!await authService.ValidatePasswordAsync(request.NewPassword))
            return BadRequest(new { error = AuthService.PasswordPolicyMessage });

        var success = await authService.ResetPasswordWithTokenAsync(request.Token, request.NewPassword);
        if (!success)
            return BadRequest(new { error = "This reset link is invalid or has expired. Request a new one." });

        return Ok(new { message = "Password reset successful" });
    }

    // Prefer the configured public origin (matches the links in release emails); fall back
    // to the request's own origin for local dev where PUBLIC_BASE_URL isn't set.
    private string ResolveBaseUrl()
    {
        var configured = config["PUBLIC_BASE_URL"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');
        return $"{Request.Scheme}://{Request.Host}";
    }

    private static (string subject, string html, string text) BuildResetEmail(string link)
    {
        const string subject = "Indigo Swallow — reset your password";
        var html = $"""
            <p>We received a request to reset your <strong>Indigo Swallow</strong> password.</p>
            <p><a href="{link}">Reset your password</a></p>
            <p>This link expires in one hour and can be used once. If you didn't request this, you can ignore this email.</p>
            """;
        var text =
            "We received a request to reset your Indigo Swallow password.\n\n" +
            $"{link}\n\n" +
            "This link expires in one hour and can be used once. If you didn't request this, you can ignore this email.";
        return (subject, html, text);
    }
}
