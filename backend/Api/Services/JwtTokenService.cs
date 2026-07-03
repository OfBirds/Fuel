using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Api.Services;

public interface ITokenService
{
    /// <summary>Issues a signed JWT carrying the user's id (<c>sub</c>) and email.</summary>
    string CreateToken(Guid userId, string email);
}

/// <summary>
/// Issues HMAC-SHA256 signed JWTs. The signing key and lifetime are resolved once at
/// startup (see <c>Program.cs</c>) from <c>JWT_SIGNING_KEY</c> / <c>JWT_EXPIRY_DAYS</c>.
/// Issuer and audience are both "Fuel" — single-origin app, no external token consumers.
/// </summary>
public class JwtTokenService(SymmetricSecurityKey signingKey, TimeSpan lifetime) : ITokenService
{
    public const string Issuer = "Fuel";
    public const string Audience = "Fuel";

    public string CreateToken(Guid userId, string email)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.Add(lifetime),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
            ]),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
