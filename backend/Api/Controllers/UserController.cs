using Api.DTOs;
using Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/user/{userId}")]
public class UserController(AppDbContext db) : ControllerBase
{
    [HttpGet("prefs")]
    public async Task<ActionResult<UserPrefsResponse>> GetPrefs(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return NotFound();

        return Ok(ToResponse(user));
    }

    [HttpPut("prefs")]
    public async Task<ActionResult<UserPrefsResponse>> UpdatePrefs(
        Guid userId, [FromBody] UpdateUserPrefsRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return NotFound();

        user.NotifyReleases = request.NotifyReleases;
        user.DailyCalorieGoal = request.DailyCalorieGoal;
        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(user));
    }

    private static UserPrefsResponse ToResponse(Models.User user) => new()
    {
        NotifyReleases = user.NotifyReleases,
        DailyCalorieGoal = user.DailyCalorieGoal,
    };
}
