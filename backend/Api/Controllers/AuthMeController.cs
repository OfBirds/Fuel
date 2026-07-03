using Api.Data;
using Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Controllers;

/// <summary>
/// "Who am I" for the current bearer token. Separate from <see cref="AuthController"/>
/// (which is <c>[AllowAnonymous]</c>) so this stays behind the default auth policy.
/// The SPA calls it right after an OIDC callback to learn its <b>Fuel</b> <c>User.Id</c>
/// (the OIDC token's own <c>sub</c> is the IdP subject; <c>OidcUserProvisioner</c> has
/// already rewritten the principal's <c>sub</c> to the Fuel GUID by the time we read it).
/// </summary>
[ApiController]
[Route("api/auth/me")]
public class AuthMeController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AuthResponse>> Me(CancellationToken ct)
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        return Ok(new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Token = string.Empty, // caller already holds the token; echoing identity only
            NotifyReleases = user.NotifyReleases,
        });
    }
}
