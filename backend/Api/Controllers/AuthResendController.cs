using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Controllers;

/// <summary>
/// Resends the CrimsonRaven email-verification mail for a <b>held</b> sign-in (unverified email).
/// The held principal still carries the IdP <c>sub</c> (numeric Zitadel user id — not yet rewritten to
/// a GUID), which is who we resend for. We call CrimsonRaven's self-service
/// <c>POST /v2/users/{sub}/email/resend</c> with a <b>role-less</b> machine PAT (<c>CR_MAILER_PAT</c>):
/// the user's own JWT can't be used (CrimsonRaven's API rejects it over the instance's http://…:443
/// audience), but an opaque PAT validates directly. The PAT has no privileges — <c>ResendEmailCode</c> is
/// permission "authenticated" — so it can only trigger verification mail. See the CrimsonRaven repo's
/// <c>docs/app-email-verification-resend.md</c>. Exempted from EmailVerificationHoldMiddleware so a held
/// user can reach it.
/// </summary>
[ApiController]
[Route("api/auth/resend-verification")]
public class AuthResendController(
    IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<AuthResendController> logger)
    : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Resend(CancellationToken ct)
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        // Held users have the numeric IdP sub; a mapped (verified) user has a GUID and doesn't need this.
        if (string.IsNullOrEmpty(sub) || Guid.TryParse(sub, out _))
            return BadRequest(new { error = "not_applicable", message = "Nothing to verify for this session." });

        var authority = config["OIDC_AUTHORITY"];
        var pat = config["CR_MAILER_PAT"];
        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(pat))
        {
            logger.LogError("Resend verification not configured (OIDC_AUTHORITY/CR_MAILER_PAT missing)");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "resend_unavailable", message = "Verification email is temporarily unavailable." });
        }

        var client = httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{authority.TrimEnd('/')}/v2/users/{sub}/email/resend");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pat}");
        req.Content = new StringContent("{\"sendCode\":{}}", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            logger.LogWarning("ResendEmailCode for {Sub} failed: {Status} {Body}", sub, (int)resp.StatusCode, body);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "send_failed", message = "Could not send the verification email. Please try again." });
        }
        return Ok(new { message = "Verification email sent." });
    }
}
