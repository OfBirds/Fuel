using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/user/{userId}")]
public class WeightController(AppDbContext db) : ControllerBase
{
    /// <summary>List weigh-ins newest first, with % delta vs the adjacent (older) entry.</summary>
    [HttpGet("weights")]
    public async Task<ActionResult<List<WeightEntryResponse>>> GetWeights(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        var entries = await db.WeightEntries
            .Where(we => we.UserId == userId)
            .OrderByDescending(we => we.RecordedAtUtc)
            .ToListAsync(ct);

        var result = new List<WeightEntryResponse>(entries.Count);
        // Compute delta vs the chronologically previous (older) entry
        // Since list is newest-first, "previous" = next in the list (older)
        for (int i = 0; i < entries.Count; i++)
        {
            double? delta = null;
            if (i < entries.Count - 1)
            {
                var prev = entries[i + 1]; // older entry
                if (prev.Weight != 0)
                    delta = Math.Round(((entries[i].Weight - prev.Weight) / prev.Weight) * 100, 1);
            }

            result.Add(new WeightEntryResponse
            {
                Id = entries[i].Id,
                Weight = entries[i].Weight,
                RecordedAtUtc = entries[i].RecordedAtUtc,
                DeltaPercent = delta,
            });
        }

        return Ok(result);
    }

    /// <summary>Record a new weigh-in.</summary>
    [HttpPost("weights")]
    public async Task<ActionResult<WeightEntryResponse>> CreateWeight(
        Guid userId, [FromBody] CreateWeightEntryRequest request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        if (request.Weight <= 0)
            return BadRequest(new { error = "Weight must be greater than zero." });

        var entry = new WeightEntry
        {
            UserId = userId,
            Weight = request.Weight,
            RecordedAtUtc = request.RecordedAtUtc ?? DateTime.UtcNow,
        };

        db.WeightEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetWeights), new { userId }, new WeightEntryResponse
        {
            Id = entry.Id,
            Weight = entry.Weight,
            RecordedAtUtc = entry.RecordedAtUtc,
            DeltaPercent = null,
        });
    }

    /// <summary>Delete a weigh-in.</summary>
    [HttpDelete("weights/{weightId:guid}")]
    public async Task<ActionResult> DeleteWeight(Guid userId, Guid weightId, CancellationToken ct)
    {
        var entry = await db.WeightEntries
            .FirstOrDefaultAsync(we => we.Id == weightId && we.UserId == userId, ct);

        if (entry is null) return NotFound();

        db.WeightEntries.Remove(entry);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }
}
