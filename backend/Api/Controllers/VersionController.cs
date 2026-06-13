using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/version")]
public class VersionController(IConfiguration config) : ControllerBase
{
    // Values are baked into the image as env vars at build time (see Dockerfile
    // ARG/ENV). Fall back to "dev"/"unknown" for local non-container runs.
    [HttpGet]
    public ActionResult Get() => Ok(new
    {
        version = config["APP_VERSION"] ?? "dev",
        commit = config["GIT_COMMIT"] ?? "unknown",
        builtAt = config["BUILD_TIME"] ?? "unknown",
    });
}
