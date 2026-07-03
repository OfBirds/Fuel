using System.Text.RegularExpressions;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/barcode")]
public partial class BarcodeController(
    AppDbContext db,
    IBarcodeFoodLookup lookup,
    ILogger<BarcodeController> logger) : ControllerBase
{
    /// <summary>Whether barcode lookup is configured and should show its affordance.</summary>
    [HttpGet("status")]
    public ActionResult Status() => Ok(new { enabled = lookup.Enabled });

    /// <summary>
    /// Resolve a numeric product barcode (EAN/UPC/GTIN) to a catalogue food. First
    /// checks our local cache (<see cref="Food.Barcode"/>); on miss, queries Open Food
    /// Facts and persists the result for future scans. Returns a fallback message when
    /// the product isn't found or lookup is disabled.
    /// </summary>
    [HttpGet("lookup/{code}")]
    public async Task<ActionResult<BarcodeLookupResponse>> Lookup(string code, CancellationToken ct)
    {
        // Basic validation: numeric, within expected GTIN length ranges
        if (string.IsNullOrWhiteSpace(code) || !BarcodePattern().IsMatch(code))
            return BadRequest(new { error = "Invalid barcode — must be 8–14 digits." });

        if (!lookup.Enabled)
            return Ok(new BarcodeLookupResponse
            {
                Found = false,
                Message = "Barcode lookup isn't configured.",
            });

        try
        {
            // Cache: if we've already resolved this barcode, short-circuit
            var cached = await db.Foods.FirstOrDefaultAsync(f => f.Barcode == code, ct);
            if (cached is not null)
                return Ok(new BarcodeLookupResponse
                {
                    Found = true,
                    IsNew = false,
                    Food = FoodController.ToResponse(cached),
                });

            // External lookup
            var match = await lookup.LookupAsync(code, ct);
            if (match is null)
                return Ok(new BarcodeLookupResponse
                {
                    Found = false,
                    Message = "Couldn't identify this product — describe it or take a photo.",
                });

            // Persist as a new catalogue food with the barcode as cache key
            var food = new Food
            {
                Name = match.Name,
                DefaultUoM = "g",
                CaloriesPerUnit = match.CaloriesPerGram,
                ProteinPerUnit = match.ProteinPerGram,
                CarbsPerUnit = match.CarbsPerGram,
                FatPerUnit = match.FatPerGram,
                Barcode = code,
            };
            db.Foods.Add(food);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Race: another request resolved this barcode between our cache check
                // and insert. Re-query the winner — it's correct and equivalent.
                var raced = await db.Foods.FirstOrDefaultAsync(f => f.Barcode == code, ct);
                if (raced is not null)
                    return Ok(new BarcodeLookupResponse
                    {
                        Found = true,
                        IsNew = false,
                        Food = FoodController.ToResponse(raced),
                    });
                // Edge: the unique-owner row was deleted mid-race — return what we created;
                // retry once by checking it was persisted.
                var ours = await db.Foods.FirstOrDefaultAsync(f => f.Barcode == code, ct);
                if (ours is not null)
                    return Ok(new BarcodeLookupResponse
                    {
                        Found = true,
                        IsNew = true,
                        Food = FoodController.ToResponse(ours),
                    });
                throw; // genuinely unexpected
            }

            return Ok(new BarcodeLookupResponse
            {
                Found = true,
                IsNew = true,
                Food = FoodController.ToResponse(food),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // client aborted — let it unwind
        }
        catch (Exception ex)
        {
            // Resilience (spec §): degrade gracefully, never block the entry screen
            logger.LogWarning(ex, "Barcode lookup failed for {Barcode}", code);
            return Ok(new BarcodeLookupResponse
            {
                Found = false,
                Message = "Couldn't identify this product — describe it or take a photo.",
            });
        }
    }

    [GeneratedRegex(@"^\d{8,14}$")]
    private static partial Regex BarcodePattern();

    /// <summary>
    /// Best-effort check for a Postgres unique-violation. Npgsql surfaces this as a
    /// <c>PostgresException</c> with <c>SqlState == "23505"</c> inside the
    /// <c>DbUpdateException</c>. Returns false when we can't confirm it is unique —
    /// safe — meaning the exception will propagate instead of a masked real error.
    /// </summary>
    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            // Npgsql.PostgresException.Data["SqlState"] is "23505" for unique_violation
            if (inner.GetType().Name == "PostgresException"
                && inner.Data["SqlState"] is string sqlState
                && sqlState == "23505")
                return true;
            inner = inner.InnerException;
        }
        return false;
    }
}
