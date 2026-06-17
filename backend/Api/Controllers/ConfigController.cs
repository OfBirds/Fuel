using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Public, non-secret runtime config for the SPA. The single Docker image is promoted
/// across stacks, so the OIDC authority/client (which differ per stack — homelab vs prod
/// CrimsonRaven) can't be baked at build time; the SPA fetches them here at startup to
/// configure its PKCE client. Only public values — no secrets.
/// </summary>
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class ConfigController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    public ActionResult Get()
    {
        var authority = config["OIDC_AUTHORITY"];
        return Ok(new
        {
            oidcEnabled = !string.IsNullOrWhiteSpace(authority),
            oidcAuthority = authority,
            oidcClientId = config["OIDC_CLIENT_ID"],
        });
    }
}
