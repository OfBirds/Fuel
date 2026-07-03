using Api.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Api.Tests;

/// <summary>
/// Verifies the signed JWTs <see cref="JwtTokenService"/> issues actually validate
/// against the configured key, carry the user id as <c>sub</c>, and are rejected when
/// the signature key differs (i.e. forged/tampered tokens).
/// </summary>
public class JwtTokenServiceTests
{
    private static SymmetricSecurityKey Key(string s) =>
        new(Encoding.UTF8.GetBytes(s));

    private const string GoodKey = "test-signing-key-at-least-32-bytes!!";

    private static TokenValidationParameters Validation(SymmetricSecurityKey key) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = JwtTokenService.Issuer,
        ValidateAudience = true,
        ValidAudience = JwtTokenService.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ValidateLifetime = true,
    };

    [Fact]
    public async Task CreateToken_ProducesTokenThatValidatesWithSubClaim()
    {
        var userId = Guid.NewGuid();
        var service = new JwtTokenService(Key(GoodKey), TimeSpan.FromDays(30));

        var token = service.CreateToken(userId, "user@example.com");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, Validation(Key(GoodKey)));
        Assert.True(result.IsValid);
        Assert.Equal(userId.ToString(), result.Claims[JwtRegisteredClaimNames.Sub]);
        Assert.Equal("user@example.com", result.Claims[JwtRegisteredClaimNames.Email]);
    }

    [Fact]
    public async Task Token_SignedWithDifferentKey_FailsValidation()
    {
        var service = new JwtTokenService(Key(GoodKey), TimeSpan.FromDays(30));
        var token = service.CreateToken(Guid.NewGuid(), "user@example.com");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token, Validation(Key("a-totally-different-32-byte-secret!!")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredToken_FailsValidation()
    {
        var service = new JwtTokenService(Key(GoodKey), TimeSpan.FromDays(-1));
        var token = service.CreateToken(Guid.NewGuid(), "user@example.com");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, Validation(Key(GoodKey)));

        Assert.False(result.IsValid);
    }
}
