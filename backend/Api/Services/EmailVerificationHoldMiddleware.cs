using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Turns the OIDC provisioner's "email unverified hold" into a clear, machine-readable 403 instead of an
/// opaque failure. When a login is held — an unverified email matching an existing account (see
/// <see cref="OidcUserProvisioner"/>) — the principal is authenticated but deliberately NOT mapped to an
/// internal user, and carries the <see cref="OidcUserProvisioner.HoldClaimType"/> marker. Such a request
/// must reach no data, so we short-circuit it here with a body the SPA can detect to show a "verify your
/// email" screen. The block is purely per-request: a later <b>verified</b> login is linked by the
/// provisioner, carries no marker, and is let through (self-heal) — no admin step.
/// </summary>
public class EmailVerificationHoldMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // The resend-verification endpoint is the held user's escape hatch — never block it.
        if (!context.Request.Path.StartsWithSegments("/api/auth/resend-verification")
            && context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(OidcUserProvisioner.HoldClaimType, OidcUserProvisioner.EmailUnverifiedHold))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "email_unverified",
                message = "Please verify your email address, then sign in again to continue.",
            }));
            return;
        }

        await next(context);
    }
}
