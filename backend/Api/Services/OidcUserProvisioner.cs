using System.Security.Claims;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Services;

/// <summary>
/// Maps a CrimsonRaven (OIDC) identity onto a Fuel <see cref="User"/> so the rest of the
/// app keeps comparing route ids to a Fuel <c>User.Id</c> GUID, unchanged. For a
/// CrimsonRaven-issued principal it:
/// <list type="number">
///   <item>finds the user by <see cref="User.ExternalSubject"/> (the IdP <c>sub</c>);</item>
///   <item>else links to an existing row by email (preserves that user's entries/weights/profile —
///   the Fuel <c>User.Id</c> never changes across IdP/instance migrations);</item>
///   <item>else creates a new password-less user.</item>
/// </list>
/// It then rewrites the principal's <c>sub</c> to the Fuel <c>User.Id</c>. Locally-issued
/// tokens (a different issuer) are skipped, which also makes this idempotent.
/// <para>
/// Email verification is owned by CrimsonRaven (Keycloak's <c>verifyEmail</c> required action gates
/// login before a token is ever issued), so there's no app-side hold here — a token reaching us has a
/// verified address. Keycloak carries <c>email</c>/<c>email_verified</c> in the token; the userinfo
/// call is only a fallback for IdPs that omit them.
/// </para>
/// </summary>
public class OidcUserProvisioner(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration config,
    ILogger<OidcUserProvisioner> logger) : IClaimsTransformation
{
    /// <summary>Marker stamped once a principal has been mapped, so the possibly-multiple
    /// IClaimsTransformation invocations per request stay idempotent.</summary>
    private const string ProvisionedClaim = "cr_provisioned";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return principal;

        // Already mapped this principal — nothing to do (idempotent across re-invocations).
        if (identity.HasClaim(ProvisionedClaim, "1"))
            return principal;

        var sub = identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(sub))
            return principal;

        // Only CrimsonRaven (OIDC) principals get mapped; locally-issued app JWTs (a different issuer,
        // sub already = User.Id) are left alone. Discriminate by ISSUER, not sub shape — Keycloak subjects
        // are GUIDs, so the old `Guid.TryParse(sub)` test no longer tells external from local apart.
        var authority = config["OIDC_AUTHORITY"]?.TrimEnd('/');
        var iss = identity.FindFirst("iss")?.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(authority)
            || !string.Equals(iss, authority, StringComparison.OrdinalIgnoreCase))
            return principal;

        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == sub);
        if (user is null)
        {
            // First login for this subject: get the email. Prefer claims (cheap), fall back to userinfo.
            var email = ReadEmailClaim(identity);
            if (string.IsNullOrWhiteSpace(email))
                email = await FetchUserinfoEmailAsync(sub);

            // Link to an existing row by email (data preserved across the IdP move), else create one.
            User? emailOwner = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                var lowered = email.ToLowerInvariant();
                emailOwner = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == lowered);
            }

            if (emailOwner is not null)
            {
                logger.LogInformation("Linking CrimsonRaven sub {Sub} to existing user {UserId} by email", sub, emailOwner.Id);
                emailOwner.ExternalSubject = sub;
                user = emailOwner;
            }
            else
            {
                user = new User { Email = email ?? sub, ExternalSubject = sub, PasswordHash = null };
                db.Users.Add(user);
            }
            await db.SaveChangesAsync();
        }

        // Swap the IdP subject for the Fuel User.Id so ResourceOwnershipFilter and every
        // api/user/{userId} route work without change.
        foreach (var c in identity.FindAll(JwtRegisteredClaimNames.Sub).ToList())
            identity.RemoveClaim(c);
        identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()));
        identity.AddClaim(new Claim(ProvisionedClaim, "1"));
        return principal;
    }

    private static string? ReadEmailClaim(ClaimsIdentity identity) =>
        identity.FindFirst(JwtRegisteredClaimNames.Email)?.Value
        ?? identity.FindFirst(ClaimTypes.Email)?.Value;

    /// <summary>Resolve email from the IdP userinfo endpoint using the caller's bearer token, for
    /// IdPs whose access token doesn't carry it (Keycloak usually does, so this rarely fires).</summary>
    private async Task<string?> FetchUserinfoEmailAsync(string sub)
    {
        var authority = config["OIDC_AUTHORITY"];
        var raw = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(raw)
            || !raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var client = httpClientFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{authority.TrimEnd('/')}/protocol/openid-connect/userinfo");
            req.Headers.TryAddWithoutValidation("Authorization", raw);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            // sanity: userinfo sub should match the token sub
            if (root.TryGetProperty("sub", out var s) && s.GetString() != sub) return null;
            return root.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch
        {
            return null; // userinfo unreachable → treat as unlinkable (new user)
        }
    }
}
