using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/unsubscribe")]
[AllowAnonymous] // clicked straight from an email link, scoped by its own token
public class UnsubscribeController(AppDbContext db) : ControllerBase
{
    // GET because it's clicked straight from an email link. Idempotent and
    // token-scoped, so re-clicks (or a client prefetch) just re-confirm.
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Guid token, CancellationToken ct)
    {
        if (token == Guid.Empty)
            return BadRequest("Missing unsubscribe token.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.UnsubscribeToken == token, ct);
        if (user is null)
            return NotFound("Unknown unsubscribe link.");

        if (user.NotifyReleases)
        {
            user.NotifyReleases = false;
            await db.SaveChangesAsync(ct);
        }

        return Content("You've been unsubscribed from Fuel release emails.", "text/plain");
    }
}
