using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/user/{userId}")]
public class EntryController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Get a user's entries within a UTC instant range [from, to). Callers compute the
    /// range from the viewer's local day so the day view is timezone-aware while storage
    /// stays UTC. Falls back to a UTC calendar day (<paramref name="date"/>, or today).
    /// </summary>
    [HttpGet("entries")]
    public async Task<ActionResult<List<EntryResponse>>> GetEntries(
        Guid userId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? date,
        CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return NotFound();

        DateTime startUtc, endUtc;
        if (from.HasValue && to.HasValue)
        {
            // Normalize to UTC: query-string binding parses the trailing 'Z' to local time.
            startUtc = from.Value.ToUniversalTime();
            endUtc = to.Value.ToUniversalTime();
        }
        else
        {
            var targetDate = DateTime.UtcNow.Date;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsed))
                targetDate = parsed.Date;
            startUtc = DateTime.SpecifyKind(targetDate, DateTimeKind.Utc); // midnight UTC
            endUtc = startUtc.AddDays(1);
        }

        var entries = await db.FoodEntries
            .Where(e => e.UserId == userId && e.IntakeAtUtc >= startUtc && e.IntakeAtUtc < endUtc)
            .OrderBy(e => e.IntakeAtUtc)
            .Select(e => ToResponse(e))
            .ToListAsync(ct);

        return Ok(entries);
    }

    /// <summary>Get a single entry by id (used when editing, regardless of its day).</summary>
    [HttpGet("entries/{entryId:guid}")]
    public async Task<ActionResult<EntryResponse>> GetEntry(
        Guid userId, Guid entryId, CancellationToken ct)
    {
        var entry = await db.FoodEntries
            .FirstOrDefaultAsync(e => e.Id == entryId && e.UserId == userId, ct);

        if (entry is null)
            return NotFound();

        return Ok(ToResponse(entry));
    }

    /// <summary>Create a food entry. Nutrition values are snapshotted as provided.</summary>
    [HttpPost("entries")]
    public async Task<ActionResult<EntryResponse>> CreateEntry(
        Guid userId, [FromBody] CreateEntryRequest request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.FoodName))
            return BadRequest(new { error = "FoodName is required." });
        if (string.IsNullOrWhiteSpace(request.MealType))
            return BadRequest(new { error = "MealType is required." });
        if (!Enum.TryParse<MealType>(request.MealType, out var mealType))
            return BadRequest(new { error = $"Invalid MealType: {request.MealType}." });
        if (string.IsNullOrWhiteSpace(request.UoM))
            return BadRequest(new { error = "UoM is required." });

        // Validate food reference if provided
        if (request.FoodId.HasValue)
        {
            var food = await db.Foods.FindAsync([request.FoodId.Value], ct);
            if (food is null)
                return BadRequest(new { error = $"Food {request.FoodId} not found." });
        }

        var entry = new FoodEntry
        {
            UserId = userId,
            FoodId = request.FoodId,
            FoodName = request.FoodName,
            IntakeAtUtc = request.IntakeAtUtc ?? DateTime.UtcNow,
            MealType = mealType,
            Quantity = request.Quantity,
            UoM = request.UoM,
            Calories = request.Calories,
            Protein = request.Protein,
            Carbs = request.Carbs,
            Fat = request.Fat,
            Source = EntrySource.Manual,
        };

        db.FoodEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetEntries), new { userId }, ToResponse(entry));
    }

    /// <summary>Update an existing entry. All fields are optional (partial update).</summary>
    [HttpPut("entries/{entryId:guid}")]
    public async Task<ActionResult<EntryResponse>> UpdateEntry(
        Guid userId, Guid entryId, [FromBody] UpdateEntryRequest request, CancellationToken ct)
    {
        var entry = await db.FoodEntries
            .FirstOrDefaultAsync(e => e.Id == entryId && e.UserId == userId, ct);

        if (entry is null)
            return NotFound();

        if (request.FoodId.HasValue)
        {
            var food = await db.Foods.FindAsync([request.FoodId.Value], ct);
            if (food is null)
                return BadRequest(new { error = $"Food {request.FoodId} not found." });
            entry.FoodId = request.FoodId;
            entry.FoodName = food.Name;
        }

        if (request.FoodName is not null)
            entry.FoodName = request.FoodName;
        if (request.IntakeAtUtc.HasValue)
            entry.IntakeAtUtc = request.IntakeAtUtc.Value;
        if (request.MealType is not null)
        {
            if (!Enum.TryParse<MealType>(request.MealType, out var mealType))
                return BadRequest(new { error = $"Invalid MealType: {request.MealType}." });
            entry.MealType = mealType;
        }
        if (request.Quantity.HasValue)
            entry.Quantity = request.Quantity.Value;
        if (request.UoM is not null)
            entry.UoM = request.UoM;
        if (request.Calories.HasValue)
            entry.Calories = request.Calories.Value;
        if (request.Protein.HasValue)
            entry.Protein = request.Protein;
        if (request.Carbs.HasValue)
            entry.Carbs = request.Carbs;
        if (request.Fat.HasValue)
            entry.Fat = request.Fat;

        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(entry));
    }

    /// <summary>Delete an entry.</summary>
    [HttpDelete("entries/{entryId:guid}")]
    public async Task<ActionResult> DeleteEntry(Guid userId, Guid entryId, CancellationToken ct)
    {
        var entry = await db.FoodEntries
            .FirstOrDefaultAsync(e => e.Id == entryId && e.UserId == userId, ct);

        if (entry is null)
            return NotFound();

        db.FoodEntries.Remove(entry);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    private static EntryResponse ToResponse(FoodEntry e) => new()
    {
        Id = e.Id,
        FoodId = e.FoodId,
        FoodName = e.FoodName,
        IntakeAtUtc = e.IntakeAtUtc,
        MealType = e.MealType.ToString(),
        Quantity = e.Quantity,
        UoM = e.UoM,
        Calories = e.Calories,
        Protein = e.Protein,
        Carbs = e.Carbs,
        Fat = e.Fat,
        Source = e.Source.ToString(),
        AiConfidence = e.AiConfidence,
    };
}
