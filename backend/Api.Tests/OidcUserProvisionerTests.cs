using System.Security.Claims;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Tests;

/// <summary>
/// OidcUserProvisioner — the link-by-email identity mapping. Verifies an existing Fuel user is reused
/// (data preserved) on an email match, that the IdP subject resolves on later logins, and that the
/// principal's `sub` is rewritten to the Fuel User.Id (so ResourceOwnershipFilter + routes are
/// unchanged). Locally-issued / already-mapped principals (a different issuer) are left untouched.
/// Email verification is gated upstream by CrimsonRaven/Keycloak, so there's no app-side hold here.
/// </summary>
public class OidcUserProvisionerTests : IDisposable
{
    private const string Issuer = "https://idp-staging.example.com/realms/crimsonraven";
    private readonly AppDbContext _db;
    private readonly Guid _aliceId;

    public OidcUserProvisionerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"oidc_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        var alice = new User { Email = "alice@example.com", PasswordHash = "hash" };
        _db.Users.Add(alice);
        _db.SaveChanges();
        _aliceId = alice.Id;
    }

    public void Dispose() => _db.Dispose();

    // These tests supply email via claims, so the userinfo fallback (which needs the HTTP
    // deps) is never exercised — empty stubs suffice. No HttpContext → userinfo no-ops.
    private OidcUserProvisioner Provisioner() => new(
        _db, new StubHttpClientFactory(), new HttpContextAccessor(),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OIDC_AUTHORITY"] = Issuer })
            .Build(),
        NullLogger<OidcUserProvisioner>.Instance);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static ClaimsPrincipal Principal(string sub, string? email, string iss = Issuer)
    {
        var claims = new List<Claim> { new("iss", iss), new(JwtRegisteredClaimNames.Sub, sub) };
        if (email is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Email, email));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }

    private static string? Sub(ClaimsPrincipal p) => p.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

    [Fact]
    public async Task EmailMatch_LinksExistingUser_PreservingId()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("kc-sub-1", "alice@example.com"));

        Assert.Equal(_aliceId.ToString(), Sub(result));            // sub rewritten to Fuel id
        var alice = await _db.Users.FindAsync(_aliceId);
        Assert.Equal("kc-sub-1", alice!.ExternalSubject);          // linked
        Assert.Equal(1, await _db.Users.CountAsync());             // no new user created
    }

    [Fact]
    public async Task EmailMatch_IsCaseInsensitive()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("kc-sub-1", "ALICE@Example.com"));

        Assert.Equal(_aliceId.ToString(), Sub(result));
    }

    [Fact]
    public async Task NoExistingEmail_CreatesPasswordlessUser()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("kc-sub-3", "newcomer@example.com"));

        var created = await _db.Users.FirstAsync(u => u.ExternalSubject == "kc-sub-3");
        Assert.Equal(created.Id.ToString(), Sub(result));
        Assert.Null(created.PasswordHash);
        Assert.Equal(2, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task SecondLogin_ResolvesByExternalSubject()
    {
        var provisioner = Provisioner();
        await provisioner.TransformAsync(Principal("kc-sub-1", "alice@example.com"));

        // Later token: same subject, email now absent — still resolves to alice via ExternalSubject.
        var again = await provisioner.TransformAsync(Principal("kc-sub-1", email: null));

        Assert.Equal(_aliceId.ToString(), Sub(again));
        Assert.Equal(1, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task LocallyIssuedOrAlreadyMappedPrincipal_IsUntouched()
    {
        // A different issuer (Fuel-issued token, or a principal already transformed) is a no-op.
        var guidSub = _aliceId.ToString();
        var result = await Provisioner()
            .TransformAsync(Principal(guidSub, "alice@example.com", iss: "Fuel"));

        Assert.Equal(guidSub, Sub(result));
        var alice = await _db.Users.FindAsync(_aliceId);
        Assert.Null(alice!.ExternalSubject); // not re-linked
        Assert.Equal(1, await _db.Users.CountAsync());
    }
}
