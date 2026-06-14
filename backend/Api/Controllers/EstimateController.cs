using Api.Config;
using Api.Data;
using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/user/{userId}")]
public class EstimateController(
    AppDbContext db,
    INutritionEstimator estimator,
    AiOptions options,
    ILogger<EstimateController> logger) : ControllerBase
{
    /// <summary>Whether AI affordances should render (lets the SPA disable them when off).</summary>
    [HttpGet("/api/ai/status")]
    public ActionResult AiStatus()
        => Ok(new { enabled = options.Enabled, supportsImages = options.Enabled && estimator.SupportsImages });

    /// <summary>
    /// Estimate nutrition from a typed description → editable review rows. Side-effect
    /// free: matches items to existing catalogue foods by name but writes nothing
    /// (refine re-issues this repeatedly; new foods are created later, on save).
    /// </summary>
    [HttpPost("estimate/text")]
    public async Task<ActionResult<EstimateResponse>> EstimateText(
        Guid userId, [FromBody] EstimateTextRequest request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        if (!options.Enabled)
            return Ok(EstimateResponse.Unavailable("AI estimation is turned off."));
        if (string.IsNullOrWhiteSpace(request.Description))
            return BadRequest(new { error = "Description is required." });

        try
        {
            var estimate = await estimator.EstimateFromTextAsync(
                request.Description, request.Notes ?? [], ct);
            var rows = await ResolveAsync(estimate, ct);
            return Ok(new EstimateResponse
            {
                Ok = true,
                OverallConfidence = estimate.OverallConfidence,
                Source = "AiText",
                Items = rows,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user hit Cancel / client aborted — let it unwind, the connection is gone
        }
        catch (Exception ex)
        {
            // Timeout, transient failure after retry, bad JSON, AI disabled — best-effort,
            // so degrade to manual rather than erroring (docs/ai-providers.md §Resilience).
            logger.LogWarning(ex, "Text estimation failed for user {UserId}", userId);
            return Ok(EstimateResponse.Unavailable("Couldn't estimate — enter it manually."));
        }
    }

    /// <summary>Resolve each estimated item to an existing catalogue food by exact
    /// (case-insensitive, paren-stripped) name, else flag it new. Read-only. Fuzzy
    /// match / dedup is a later optimization (docs/food-catalogue-and-logging.md).</summary>
    private async Task<List<EstimateRow>> ResolveAsync(NutritionEstimate estimate, CancellationToken ct)
    {
        var rows = new List<EstimateRow>(estimate.Items.Count);
        foreach (var item in estimate.Items)
        {
            var normal = NormalizeName(item.Name);
            var match = await db.Foods
                .Where(f => f.Name.ToLower() == normal)
                .Select(f => new { f.Id, f.DefaultUoM })
                .FirstOrDefaultAsync(ct);

            rows.Add(new EstimateRow
            {
                Name = item.Name,
                Quantity = item.Quantity,
                Uom = item.Uom,
                Calories = item.Calories,
                Protein = item.Protein,
                Carbs = item.Carbs,
                Fat = item.Fat,
                Confidence = item.Confidence,
                MatchedFoodId = match?.Id,
                MatchedDefaultUoM = match?.DefaultUoM,
                IsNew = match is null,
            });
        }
        return rows;
    }

    /// <summary>Normalize an AI-returned food name for matching: lowercase, strip
    /// parenthetical qualifiers like "(groß)" or "(fried)", collapse whitespace.</summary>
    private static string NormalizeName(string raw)
    {
        var s = raw.ToLowerInvariant();
        // Strip parenthetical qualifiers — "Schnitzel (groß)" → "schnitzel"
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        return s.Trim();
    }
}
