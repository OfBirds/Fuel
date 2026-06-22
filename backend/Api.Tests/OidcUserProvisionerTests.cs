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
/// OidcUserProvisioner — the link-by-email identity mapping. Verifies an existing Fuel
/// user is reused (data preserved) on a verified-email match, that unverified emails do
/// NOT auto-link, that the IdP subject resolves on later logins, and that the principal's
/// `sub` is rewritten to the Fuel User.Id (so ResourceOwnershipFilter + routes are
/// unchanged). Fuel-issued / already-mapped (GUID-sub) principals are left untouched.
/// </summary>
public class OidcUserProvisionerTests : IDisposable
{
    private const string Issuer = "http://192.168.4.55:9100";
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

    private static ClaimsPrincipal Principal(string sub, string? email, bool emailVerified, string iss = Issuer)
    {
        var claims = new List<Claim> { new("iss", iss), new(JwtRegisteredClaimNames.Sub, sub) };
        if (email is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Email, email));
        claims.Add(new Claim("email_verified", emailVerified ? "true" : "false"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }

    private static string? Sub(ClaimsPrincipal p) => p.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

    [Fact]
    public async Task VerifiedEmailMatch_LinksExistingUser_PreservingId()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("zitadel-sub-1", "alice@example.com", emailVerified: true));

        Assert.Equal(_aliceId.ToString(), Sub(result));            // sub rewritten to Fuel id
        var alice = await _db.Users.FindAsync(_aliceId);
        Assert.Equal("zitadel-sub-1", alice!.ExternalSubject);     // linked
        Assert.Equal(1, await _db.Users.CountAsync());             // no new user created
    }

    [Fact]
    public async Task VerifiedEmailMatch_IsCaseInsensitive()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("zitadel-sub-1", "ALICE@Example.com", emailVerified: true));

        Assert.Equal(_aliceId.ToString(), Sub(result));
    }

    [Fact]
    public async Task UnverifiedEmail_MatchingExistingUser_Holds_PersistsNothing()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("zitadel-sub-2", "alice@example.com", emailVerified: false));

        Assert.Equal("zitadel-sub-2", Sub(result));   // sub NOT rewritten → unmapped, can reach no data
        Assert.True(result.HasClaim(                   // marker → EmailVerificationHoldMiddleware returns a clear 403
            OidcUserProvisioner.HoldClaimType, OidcUserProvisioner.EmailUnverifiedHold));
        var alice = await _db.Users.FindAsync(_aliceId);
        Assert.Null(alice!.ExternalSubject);          // alice untouched — not claimed
        Assert.Equal(1, await _db.Users.CountAsync()); // and NO competing row that would lock out the link
    }

    [Fact]
    public async Task VerifyingLater_SelfHealsIntoExistingUser()
    {
        var provisioner = Provisioner();

        // First login, email not yet verified → held, nothing persisted.
        await provisioner.TransformAsync(Principal("zitadel-sub-2", "alice@example.com", emailVerified: false));
        Assert.Null((await _db.Users.FindAsync(_aliceId))!.ExternalSubject);
        Assert.Equal(1, await _db.Users.CountAsync());

        // User verifies in the IdP, logs in again (same sub) → links into alice's row.
        var healed = await provisioner.TransformAsync(Principal("zitadel-sub-2", "alice@example.com", emailVerified: true));

        Assert.Equal(_aliceId.ToString(), Sub(healed));
        Assert.False(healed.HasClaim(                 // hold cleared → middleware lets it through
            OidcUserProvisioner.HoldClaimType, OidcUserProvisioner.EmailUnverifiedHold));
        Assert.Equal("zitadel-sub-2", (await _db.Users.FindAsync(_aliceId))!.ExternalSubject);
        Assert.Equal(1, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task UnverifiedEmail_WithNoExistingUser_IsStillProvisioned()
    {
        var result = await Provisioner()
            .TransformAsync(Principal("zitadel-sub-3", "newcomer@example.com", emailVerified: false));

        // Nobody owns this email → nothing to protect; they get their own fresh row.
        var created = await _db.Users.FirstAsync(u => u.ExternalSubject == "zitadel-sub-3");
        Assert.Equal(created.Id.ToString(), Sub(result));
        Assert.Null(created.PasswordHash);
        Assert.Equal(2, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task SecondLogin_ResolvesByExternalSubject()
    {
        var provisioner = Provisioner();
        await provisioner.TransformAsync(Principal("zitadel-sub-1", "alice@example.com", emailVerified: true));

        // Later token: same subject, email now absent/unverified — still resolves to alice.
        var again = await provisioner.TransformAsync(Principal("zitadel-sub-1", email: null, emailVerified: false));

        Assert.Equal(_aliceId.ToString(), Sub(again));
        Assert.Equal(1, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task FuelIssuedOrAlreadyMappedPrincipal_IsUntouched()
    {
        // A GUID sub (Fuel-issued token, or a principal already transformed) is a no-op.
        var guidSub = _aliceId.ToString();
        var result = await Provisioner()
            .TransformAsync(Principal(guidSub, "alice@example.com", emailVerified: true, iss: "Fuel"));

        Assert.Equal(guidSub, Sub(result));
        var alice = await _db.Users.FindAsync(_aliceId);
        Assert.Null(alice!.ExternalSubject); // not re-linked
        Assert.Equal(1, await _db.Users.CountAsync());
    }
}
