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
///   <item>else links by <b>verified</b> email to an existing row (preserves that user's
///   entries/weights/profile — the Fuel <c>User.Id</c> never changes);</item>
///   <item>else, if the email is <b>unverified but already owns a row</b>, <b>holds</b> — persists
///   nothing and leaves the <c>sub</c> un-rewritten (downstream <c>Guid.TryParse</c> fails → 401/403,
///   a "verify your email" state) so a later verified login self-heals into that row instead of being
///   locked out by a competing one;</item>
///   <item>else creates a new password-less user.</item>
/// </list>
/// It then rewrites the principal's <c>sub</c> to the Fuel <c>User.Id</c>. Fuel-issued
/// tokens (GUID <c>sub</c>) are skipped, which also makes this idempotent.
/// <para>
/// Zitadel's JWT access tokens are minimal and don't carry <c>email</c>/<c>email_verified</c>,
/// so on first login (no <see cref="User.ExternalSubject"/> match yet) we resolve them from
/// the IdP <c>userinfo</c> endpoint using the caller's bearer token. Returning users hit the
/// fast <c>ExternalSubject</c> path and make no extra call.
/// </para>
/// </summary>
public class OidcUserProvisioner(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration config,
    ILogger<OidcUserProvisioner> logger) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return principal;

        var sub = identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        // A GUID sub means a Fuel-issued token or an already-mapped principal — nothing to do.
        // (Zitadel subjects are numeric snowflake ids, never GUIDs.)
        if (string.IsNullOrEmpty(sub) || Guid.TryParse(sub, out _))
            return principal;

        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == sub);
        if (user is null)
        {
            // First login for this subject: get identity details. Prefer claims (cheap),
            // fall back to userinfo since Zitadel's access token omits email.
            var (email, emailVerified) = ReadEmailClaims(identity);
            if (string.IsNullOrWhiteSpace(email))
                (email, emailVerified) = await FetchUserinfoAsync(sub);

            // Does an existing row already own this email?
            User? emailOwner = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                var lowered = email.ToLowerInvariant();
                emailOwner = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == lowered);
            }

            if (emailOwner is not null && !emailVerified)
            {
                // HOLD: an unverified address must not claim another person's data, and we must NOT
                // bind this sub to a competing row — that would make every later login hit the fast
                // ExternalSubject path and never retry the verified link, so verifying the email
                // afterwards couldn't recover the data. Persist nothing and leave the sub un-rewritten;
                // downstream Guid.TryParse fails → 401/403 ("verify your email"), and a later VERIFIED
                // login self-heals into the row. Primary guard remains the IdP (verify-on-registration).
                logger.LogWarning(
                    "Unverified login (sub {Sub}) matches existing user {UserId}; holding until the email is verified",
                    sub, emailOwner.Id);
                return principal;
            }

            if (emailOwner is not null)
            {
                emailOwner.ExternalSubject = sub; // verified → link existing row, data preserved
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
        return principal;
    }

    private static (string? email, bool verified) ReadEmailClaims(ClaimsIdentity identity)
    {
        var email = identity.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? identity.FindFirst(ClaimTypes.Email)?.Value;
        var verified = string.Equals(
            identity.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        return (email, verified);
    }

    /// <summary>Resolve email + email_verified from the IdP userinfo endpoint using the
    /// caller's bearer token (the access token alone doesn't carry them).</summary>
    private async Task<(string? email, bool verified)> FetchUserinfoAsync(string sub)
    {
        var authority = config["OIDC_AUTHORITY"];
        var raw = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(raw)
            || !raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, false);

        try
        {
            var client = httpClientFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{authority.TrimEnd('/')}/oidc/v1/userinfo");
            req.Headers.TryAddWithoutValidation("Authorization", raw);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (null, false);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            // sanity: userinfo sub should match the token sub
            if (root.TryGetProperty("sub", out var s) && s.GetString() != sub) return (null, false);
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var verified = root.TryGetProperty("email_verified", out var v)
                && (v.ValueKind == JsonValueKind.True
                    || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            return (email, verified);
        }
        catch
        {
            return (null, false); // userinfo unreachable → treat as unlinkable (new user)
        }
    }
}
