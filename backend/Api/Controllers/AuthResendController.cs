using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Controllers;

/// <summary>
/// Sends a fresh CrimsonRaven email-verification mail for a <b>held</b> sign-in (unverified email).
/// The held principal still carries the IdP <c>sub</c> (numeric Zitadel user id — not yet rewritten to a
/// GUID), which is who we send for. We call CrimsonRaven's <c>SendEmailCode</c>
/// (<c>POST /v2/users/{sub}/email/send</c>) — which <b>generates and sends</b> a new code. (We do NOT use
/// <c>ResendEmailCode</c>: it only re-sends an <i>existing</i> code and fails with "Code is empty" once it
/// has expired.) We use the <c>app-mailer</c> machine PAT (<c>CR_MAILER_PAT</c>) rather than the user's own
/// token: CrimsonRaven's API rejects a user JWT over the instance's <c>http://…:443</c> audience, but an
/// opaque PAT validates directly. Sending a code for <i>another</i> user requires <c>user.write</c>, so the
/// app-mailer holds <c>IAM_USER_MANAGER</c>. See the CrimsonRaven repo's
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
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{authority.TrimEnd('/')}/v2/users/{sub}/email/send");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pat}");
        req.Content = new StringContent("{\"sendCode\":{}}", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            logger.LogWarning("SendEmailCode for {Sub} failed: {Status} {Body}", sub, (int)resp.StatusCode, body);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "send_failed", message = "Could not send the verification email. Please try again." });
        }
        return Ok(new { message = "Verification email sent." });
    }
}
