using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>Maintenance endpoints for operators. All require authentication.</summary>
[ApiController]
[Route("api/admin")]
public class AdminController(FoodDedupService dedupService, ILogger<AdminController> logger)
    : ControllerBase
{
    /// <summary>
    /// Run the one-time duplicate-food cleanup (§5.9 / OFB-43c).
    /// Idempotent — safe to call multiple times; a second call is a no-op.
    ///
    /// Pre-condition: the non-unique NormalizedName index must exist
    /// (migration 20260714000001 applied). Post-condition: NormalizedName
    /// index can be flipped to unique (next migration).
    /// </summary>
    [HttpPost("deduplicate-foods")]
    public async Task<ActionResult<DedupResult>> DeduplicateFoods(CancellationToken ct)
    {
        logger.LogWarning("Admin: duplicate-food cleanup triggered.");
        var result = await dedupService.DeduplicateAsync(ct);

        if (result.GroupsFound == 0)
            return Ok(new { message = "No duplicate foods found — nothing to do.", result });

        logger.LogWarning(
            "Admin: dedup complete — {Groups} group(s), {Deleted} foods deleted, snapshot at {Snapshot}.",
            result.GroupsFound, result.FoodsDeleted, result.SnapshotPath);

        return Ok(new
        {
            message = $"Dedup complete: {result.FoodsDeleted} duplicate foods merged across {result.GroupsFound} group(s). " +
                      "Review the snapshot before applying the unique-index migration.",
            result,
        });
    }
}
