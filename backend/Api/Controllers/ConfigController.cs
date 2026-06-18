using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Public, non-secret runtime config for the SPA. The single Docker image is promoted
/// across stacks, so the OIDC authority/client (which differ per stack — homelab vs prod
/// CrimsonRaven) can't be baked at build time; the SPA fetches them here at startup.
/// Also reports whether CrimsonRaven is reachable (oidcOnline) and the IdP's current logo
/// URLs — scraped from CrimsonRaven's own public login page so the logo stays single-sourced
/// at the IdP (it auto-follows whenever CrimsonRaven changes it; per-env via OIDC_AUTHORITY).
/// Only public values — no secrets.
/// </summary>
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class ConfigController(IConfiguration config, IHttpClientFactory httpClientFactory) : ControllerBase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogoTtl = TimeSpan.FromMinutes(10);
    private static DateTime _checkedUtc = DateTime.MinValue, _logoCheckedUtc = DateTime.MinValue;
    private static bool _online;
    private static string? _logoUrl, _logoUrlDark;

    [HttpGet]
    public async Task<ActionResult> Get(CancellationToken ct)
    {
        var authority = config["OIDC_AUTHORITY"];
        var enabled = !string.IsNullOrWhiteSpace(authority);
        var online = enabled && await IsOnlineAsync(authority!, ct);
        if (online) await ResolveLogosAsync(authority!, ct);
        return Ok(new
        {
            oidcEnabled = enabled,
            oidcOnline = online,
            oidcAuthority = authority,
            oidcClientId = config["OIDC_CLIENT_ID"],
            oidcLogoUrl = _logoUrl,
            oidcLogoUrlDark = _logoUrlDark,
        });
    }

    /// <summary>Is CrimsonRaven's OIDC metadata reachable? Cached for <see cref="OnlineTtl"/>.</summary>
    private async Task<bool> IsOnlineAsync(string authority, CancellationToken ct)
    {
        if (DateTime.UtcNow - _checkedUtc < OnlineTtl) return _online;
        await Gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _checkedUtc < OnlineTtl) return _online;
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2.5);
            using var resp = await client.GetAsync(
                $"{authority.TrimEnd('/')}/.well-known/openid-configuration", ct);
            _online = resp.IsSuccessStatusCode;
        }
        catch { _online = false; }
        finally { _checkedUtc = DateTime.UtcNow; Gate.Release(); }
        return _online;
    }

    /// <summary>Scrape the IdP's current logo URLs from its public login page so Fuel can show
    /// the same logo without copying it. The URL carries a per-upload id, so we re-read it
    /// periodically (<see cref="LogoTtl"/>) to pick up logo changes.</summary>
    private async Task ResolveLogosAsync(string authority, CancellationToken ct)
    {
        if (DateTime.UtcNow - _logoCheckedUtc < LogoTtl && _logoUrl is not null) return;
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var html = await client.GetStringAsync($"{authority.TrimEnd('/')}/ui/v2/login/loginname", ct);
            // logo-<id> (light) and logo-dark-<id> — the page links both.
            _logoUrl = Match(html, @"https?://[^""'\\]+/policy/label/logo-\d+");
            _logoUrlDark = Match(html, @"https?://[^""'\\]+/policy/label/logo-dark-\d+");
        }
        catch { /* keep last known on failure */ }
        finally { _logoCheckedUtc = DateTime.UtcNow; }
    }

    private static string? Match(string s, string pattern)
    {
        var m = Regex.Match(s, pattern);
        return m.Success ? m.Value : null;
    }
}
