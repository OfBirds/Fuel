using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous] // login/register/reset must work before a token exists
public class AuthController(IAuthService authService, ITokenService tokenService) : ControllerBase
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
}
