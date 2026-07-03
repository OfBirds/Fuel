using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Public, non-secret runtime config for the SPA. The single Docker image is promoted
/// across stacks, so the OIDC authority/client (which differ per stack — homelab vs prod
/// CrimsonRaven) can't be baked at build time; the SPA fetches them here at startup.
/// Also reports whether CrimsonRaven is reachable (oidcOnline). Branding (logo, theme) is owned
/// by CrimsonRaven/Keycloak's own login pages, so the app no longer scrapes a logo from the IdP.
/// Only public values — no secrets.
/// </summary>
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class ConfigController(IConfiguration config, IHttpClientFactory httpClientFactory) : ControllerBase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    // Cache a positive (reachable) result longer than a negative one: a healthy IdP rarely
    // flips, but a negative is usually a transient blip (Keycloak restart, slow DNS, NAT
    // hairpin) we want to re-probe soon rather than show a false "offline" banner for a minute.
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfflineTtl = TimeSpan.FromSeconds(10);
    private static DateTime _checkedUtc = DateTime.MinValue;
    private static bool _online;

    private static bool IsFresh() =>
        DateTime.UtcNow - _checkedUtc < (_online ? OnlineTtl : OfflineTtl);

    [HttpGet]
    public async Task<ActionResult> Get(CancellationToken ct)
    {
        var authority = config["OIDC_AUTHORITY"];
        var enabled = !string.IsNullOrWhiteSpace(authority);
        var online = enabled && await IsOnlineAsync(authority!, ct);
        return Ok(new
        {
            oidcEnabled = enabled,
            oidcOnline = online,
            oidcAuthority = authority,
            oidcClientId = config["OIDC_CLIENT_ID"],
            // Login mode: 'crimsonraven' (default) → CR only; 'legacy' → the app's email/password form
            // only. A manual env break-glass (AUTH_MODE=legacy) for CR maintenance — never both at once.
            authMode = string.Equals(config["AUTH_MODE"], "legacy", StringComparison.OrdinalIgnoreCase)
                ? "legacy" : "crimsonraven",
        });
    }

    /// <summary>Is CrimsonRaven's OIDC metadata reachable? Cached for <see cref="OnlineTtl"/>
    /// (positive) / <see cref="OfflineTtl"/> (negative).</summary>
    private async Task<bool> IsOnlineAsync(string authority, CancellationToken ct)
    {
        if (IsFresh()) return _online;
        await Gate.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _online;
            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(2.5);
                using var resp = await client.GetAsync(
                    $"{authority.TrimEnd('/')}/.well-known/openid-configuration", ct);
                _online = resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller (browser) aborted mid-probe — that says nothing about Raven's
                // health. Don't record a verdict or stamp the cache; just let the request
                // unwind. (A HttpClient *timeout* throws with a different token, so it falls
                // through to the catch below and is correctly treated as offline.)
                throw;
            }
            catch
            {
                // Timeout (2.5s), DNS/connection failure, or a non-success status → offline.
                _online = false;
            }
            _checkedUtc = DateTime.UtcNow;
            return _online;
        }
        finally { Gate.Release(); }
    }
}
