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
    IEstimatorChain estimator,
    ILogger<EstimateController> logger) : ControllerBase
{
    /// <summary>Which AI affordances should render — text and photo are gated independently,
    /// since the provider chains for each are configured separately.</summary>
    [HttpGet("/api/ai/status")]
    public ActionResult AiStatus()
        => Ok(new
        {
            enabled = estimator.SupportsText || estimator.SupportsImages,
            supportsText = estimator.SupportsText,
            supportsImages = estimator.SupportsImages,
        });

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

        if (!estimator.SupportsText)
            return Ok(EstimateResponse.Unavailable("AI text estimation isn't configured."));
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

    /// <summary>
    /// Estimate nutrition from a photo → editable review rows. Same shape and resilience
    /// as <see cref="EstimateText"/>; the image is read into memory, handed to the
    /// provider, and never written to disk or DB (docs/ai-estimation.md §"Image lifetime").
    /// Refine re-issues this with the same photo + accumulated notes.
    /// </summary>
    [HttpPost("estimate/image")]
    [RequestSizeLimit(15 * 1024 * 1024)] // a phone photo is a few MB; cap well above that
    public async Task<ActionResult<EstimateResponse>> EstimateImage(
        Guid userId, [FromForm] EstimateImageRequest request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return NotFound();

        if (!estimator.SupportsImages)
            return Ok(EstimateResponse.Unavailable("Photo estimation isn't configured."));
        if (request.Image is null || request.Image.Length == 0)
            return BadRequest(new { error = "An image is required." });
        if (!request.Image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must be an image." });

        try
        {
            using var ms = new MemoryStream();
            await request.Image.CopyToAsync(ms, ct);

            var estimate = await estimator.EstimateFromImageAsync(
                ms.ToArray(), request.Image.ContentType, request.Description, request.Notes ?? [], ct);
            var rows = await ResolveAsync(estimate, ct);
            return Ok(new EstimateResponse
            {
                Ok = true,
                OverallConfidence = estimate.OverallConfidence,
                Source = "AiPhoto",
                Items = rows,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user hit Cancel / client aborted — let it unwind, the connection is gone
        }
        catch (Exception ex)
        {
            // Same degrade-to-manual policy as text (docs/ai-providers.md §Resilience).
            logger.LogWarning(ex, "Photo estimation failed for user {UserId}", userId);
            return Ok(EstimateResponse.Unavailable("Couldn't estimate from the photo — enter it manually."));
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
            var normal = FoodNameNormalizer.Normalize(item.Name);
            var match = await db.Foods
                .Where(f => f.NormalizedName == normal)
                .Select(f => new { f.Id, f.DefaultUoM })
                .FirstOrDefaultAsync(ct);

            rows.Add(new EstimateRow
            {
                Name = ToTitleCase(item.Name),
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

    // Minor words kept lowercase in the middle of a title (articles, short
    // conjunctions/prepositions). The first and last word are always capitalized.
    private static readonly HashSet<string> TitleMinorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "if", "in", "nor", "of",
        "off", "on", "or", "per", "the", "to", "up", "via", "vs", "with",
    };

    /// <summary>Title-case an AI food name for display ("scrambled eggs on toast" →
    /// "Scrambled Eggs on Toast"). Models tend to return all-lowercase; matching
    /// still goes through <see cref="FoodNameNormalizer.Normalize"/>.</summary>
    internal static string ToTitleCase(string raw)
    {
        var words = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            var minor = i != 0 && i != words.Length - 1 && TitleMinorWords.Contains(w);
            words[i] = minor || w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }
}
