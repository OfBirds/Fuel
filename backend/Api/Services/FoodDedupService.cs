using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// One-time duplicate food cleanup: snapshot → group by NormalizedName →
/// pick survivor → repoint FKs → delete orphans. Idempotent — safe to
/// run multiple times; a second run is a no-op if no duplicates remain.
///
/// Survivor selection (deterministic, in priority order):
///   1. Composite foods (has ingredients) are preferred.
///   2. Barcode-bearing foods are preferred.
///   3. Oldest CreatedAtUtc wins.
///   4. Lowest Id (Guid) breaks ties.
///
/// Edge cases handled:
///   - Self-referential FoodIngredient rows after repointing → dropped.
///   - Duplicate (ParentFoodId, ChildFoodId) links after repointing → deduped.
///   - UserFoodPriority conflicts (same user has priority for both survivor
///     and non-survivor) → lower Ponder kept.
/// </summary>
/// <remarks>
/// This is the implementation backing the OFB-43c / OFB-47 one-time cleanup.
/// Run BEFORE applying the migration that makes NormalizedName unique.
/// </remarks>
public class FoodDedupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<FoodDedupService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Run the full dedup pipeline inside a transaction. Returns a report of
    /// every action taken so the operator can verify before applying the
    /// unique-index migration.
    /// </summary>
    public async Task<DedupResult> DeduplicateAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // ── 0. Discover duplicates ──────────────────────────────────────
        var groups = await FindDuplicateGroupsAsync(db, ct);
        var result = new DedupResult { GroupsFound = groups.Count };

        if (groups.Count == 0)
        {
            logger.LogInformation("Dedup: no duplicate NormalizedName groups found — nothing to do.");
            return result;
        }

        result.TotalDuplicates = groups.Sum(g => g.Count - 1);
        logger.LogInformation("Dedup: {Groups} duplicate group(s), {Total} foods to merge.",
            groups.Count, result.TotalDuplicates);

        // ── 1. Snapshot ────────────────────────────────────────────────
        result.SnapshotPath = await SnapshotAsync(db, ct);

        // ── 2. Process each group in a transaction ─────────────────────
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var group in groups)
        {
            var detail = await ProcessGroupAsync(db, group, ct);
            result.Details.Add(detail);

            result.FoodEntriesRepointed += detail.FoodEntriesRepointed;
            result.FoodIngredientsRepointed += detail.FoodIngredientsRepointed;
            result.UserFoodPrioritiesMerged += detail.UserFoodPrioritiesMerged;
            result.SelfReferentialLinksDropped += detail.SelfReferentialLinksDropped;
            result.DuplicateLinksDropped += detail.DuplicateLinksDropped;
        }

        // ── 3. Delete orphaned non-survivors ───────────────────────────
        var allNonSurvivorIds = groups.SelectMany(g => g.Skip(1)).Select(f => f.Id).ToHashSet();
        var orphanFoods = await db.Foods
            .Where(f => allNonSurvivorIds.Contains(f.Id))
            .ToListAsync(ct);
        db.Foods.RemoveRange(orphanFoods);
        result.FoodsDeleted = orphanFoods.Count;
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Dedup complete: {FoodsDeleted} foods deleted, {Entries} entries repointed, " +
            "{Ingredients} ingredients repointed, {Priorities} priorities merged, " +
            "{SelfRef} self-refs dropped, {DupLinks} duplicate links dropped.",
            result.FoodsDeleted, result.FoodEntriesRepointed,
            result.FoodIngredientsRepointed, result.UserFoodPrioritiesMerged,
            result.SelfReferentialLinksDropped, result.DuplicateLinksDropped);

        return result;
    }

    // ── Discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Find all NormalizedName values that appear more than once, and
    /// return the full Food rows for each group ordered by survivor priority.
    /// </summary>
    internal static async Task<List<List<Food>>> FindDuplicateGroupsAsync(
        AppDbContext db, CancellationToken ct)
    {
        // Find which NormalizedName values have duplicates (ignoring empty strings).
        var dupKeys = await db.Foods
            .Where(f => f.NormalizedName != "")
            .GroupBy(f => f.NormalizedName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        if (dupKeys.Count == 0)
            return [];

        // Load all foods in duplicate groups, with ingredients for composite detection.
        var allDupFoods = await db.Foods
            .Include(f => f.Ingredients)
            .Where(f => dupKeys.Contains(f.NormalizedName))
            .ToListAsync(ct);

        // Group and sort each group by survivor priority.
        return allDupFoods
            .GroupBy(f => f.NormalizedName)
            .Select(g => SortBySurvivorPriority(g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Sort a list of foods sharing the same NormalizedName so the best
    /// survivor is first. Deterministic total order.
    /// </summary>
    internal static List<Food> SortBySurvivorPriority(List<Food> foods)
    {
        return foods
            .OrderByDescending(f => f.Ingredients.Count > 0 ? 1 : 0)  // composite preferred
            .ThenByDescending(f => f.Barcode is not null ? 1 : 0)      // barcode preferred
            .ThenBy(f => f.CreatedAtUtc)                                // oldest first
            .ThenBy(f => f.Id)                                          // tie-break
            .ToList();
    }

    // ── Per-group merge ────────────────────────────────────────────────

    private async Task<DedupGroupDetail> ProcessGroupAsync(
        AppDbContext db, List<Food> group, CancellationToken ct)
    {
        var survivor = group[0];
        var nonSurvivors = group.Skip(1).ToList();
        var nonSurvivorIds = nonSurvivors.Select(f => f.Id).ToHashSet();

        var detail = new DedupGroupDetail
        {
            NormalizedName = survivor.NormalizedName,
            SurvivorId = survivor.Id,
            SurvivorName = survivor.Name,
            MergedIds = nonSurvivors.Select(f => f.Id).ToList(),
        };

        logger.LogInformation(
            "Dedup group '{NormalizedName}': survivor={Survivor} ({SurvivorId}), merging {Count} others.",
            survivor.NormalizedName, survivor.Name, survivor.Id, nonSurvivors.Count);

        // ── Repoint FoodEntry.FoodId ─────────────────────────────────
        var entriesToRepoint = await db.FoodEntries
            .Where(e => e.FoodId.HasValue && nonSurvivorIds.Contains(e.FoodId.Value))
            .ToListAsync(ct);
        foreach (var entry in entriesToRepoint)
            entry.FoodId = survivor.Id;
        detail.FoodEntriesRepointed = entriesToRepoint.Count;

        // ── Repoint FoodIngredient.ChildFoodId ───────────────────────
        var childLinks = await db.FoodIngredients
            .Where(fi => nonSurvivorIds.Contains(fi.ChildFoodId))
            .ToListAsync(ct);
        foreach (var fi in childLinks)
            fi.ChildFoodId = survivor.Id;

        // ── Repoint FoodIngredient.ParentFoodId ──────────────────────
        var parentLinks = await db.FoodIngredients
            .Where(fi => nonSurvivorIds.Contains(fi.ParentFoodId))
            .ToListAsync(ct);
        foreach (var fi in parentLinks)
            fi.ParentFoodId = survivor.Id;

        detail.FoodIngredientsRepointed = childLinks.Count + parentLinks.Count;

        // Save all repoints so far so subsequent queries see the new state.
        await db.SaveChangesAsync(ct);

        // ── Merge UserFoodPriority (keep lower Ponder on conflict) ───
        detail.UserFoodPrioritiesMerged =
            await MergeUserFoodPrioritiesAsync(db, survivor.Id, nonSurvivorIds, ct);

        // ── Drop self-referential FoodIngredient rows ────────────────
        var selfRefs = await db.FoodIngredients
            .Where(fi => fi.ParentFoodId == fi.ChildFoodId)
            .ToListAsync(ct);
        detail.SelfReferentialLinksDropped = selfRefs.Count;
        if (selfRefs.Count > 0)
        {
            db.FoodIngredients.RemoveRange(selfRefs);
            await db.SaveChangesAsync(ct);
        }

        // ── Drop duplicate (ParentFoodId, ChildFoodId) links ─────────
        detail.DuplicateLinksDropped =
            await DedupeFoodIngredientLinksAsync(db, ct);

        return detail;
    }

    // ── UserFoodPriority merge ─────────────────────────────────────────

    /// <summary>
    /// Move UserFoodPriority rows from non-survivors to the survivor.
    /// When the same user already has a priority for the survivor, keep
    /// the lower Ponder value (higher priority) and drop the other row.
    /// </summary>
    private static async Task<int> MergeUserFoodPrioritiesAsync(
        AppDbContext db, Guid survivorId, HashSet<Guid> nonSurvivorIds, CancellationToken ct)
    {
        var merged = 0;

        // Load priorities for non-survivors.
        var nonSurvivorPriorities = await db.UserFoodPriorities
            .Where(p => nonSurvivorIds.Contains(p.FoodId))
            .ToListAsync(ct);

        if (nonSurvivorPriorities.Count == 0)
            return 0;

        // Load existing priorities for the survivor so we can resolve conflicts.
        var survivorUserIds = await db.UserFoodPriorities
            .Where(p => p.FoodId == survivorId)
            .Select(p => p.UserId)
            .ToHashSetAsync(ct);

        foreach (var prio in nonSurvivorPriorities)
        {
            if (survivorUserIds.Contains(prio.UserId))
            {
                // Conflict: same user has priority for both survivor and non-survivor.
                // Keep the lower Ponder.
                var survivorPrio = await db.UserFoodPriorities
                    .FirstAsync(p => p.FoodId == survivorId && p.UserId == prio.UserId, ct);
                if (prio.Ponder < survivorPrio.Ponder)
                    survivorPrio.Ponder = prio.Ponder;
                // Delete the non-survivor's row (cascade would catch it, but be explicit).
                db.UserFoodPriorities.Remove(prio);
            }
            else
            {
                // No conflict: just repoint to survivor.
                prio.FoodId = survivorId;
                survivorUserIds.Add(prio.UserId); // track for subsequent non-survivors in same group
            }

            merged++;
        }

        await db.SaveChangesAsync(ct);
        return merged;
    }

    // ── Duplicate link cleanup ─────────────────────────────────────────

    /// <summary>
    /// After repointing, some (ParentFoodId, ChildFoodId) pairs may appear
    /// more than once. Keep exactly one row per distinct pair and delete extras.
    /// </summary>
    private static async Task<int> DedupeFoodIngredientLinksAsync(
        AppDbContext db, CancellationToken ct)
    {
        var dropped = 0;

        // Find duplicate pairs — raw SQL is cleanest for this.
        // Strategy: find all (ParentFoodId, ChildFoodId) pairs with count > 1,
        // then for each pair keep the row with the lowest Id and delete the rest.

        var dupPairs = await db.FoodIngredients
            .GroupBy(fi => new { fi.ParentFoodId, fi.ChildFoodId })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.ParentFoodId, g.Key.ChildFoodId })
            .ToListAsync(ct);

        foreach (var pair in dupPairs)
        {
            // Find all rows for this pair, ordered by Id ascending.
            var rows = await db.FoodIngredients
                .Where(fi => fi.ParentFoodId == pair.ParentFoodId
                          && fi.ChildFoodId == pair.ChildFoodId)
                .OrderBy(fi => fi.Id)
                .ToListAsync(ct);

            // Keep the first (lowest Id), delete the rest.
            var keep = rows[0];
            var toDelete = rows.Skip(1).ToList();
            db.FoodIngredients.RemoveRange(toDelete);
            dropped += toDelete.Count;
        }

        if (dropped > 0)
            await db.SaveChangesAsync(ct);

        return dropped;
    }

    // ── Snapshot ───────────────────────────────────────────────────────

    /// <summary>
    /// Write a JSON snapshot of Food, FoodEntry, and FoodIngredient tables
    /// before mutation. This is the rollback path — do not skip it.
    /// Follows the BackupService conventions (camelCase, timestamped file).
    /// </summary>
    private async Task<string> SnapshotAsync(AppDbContext db, CancellationToken ct)
    {
        var dir = config["BACKUP_DIR"];
        if (string.IsNullOrWhiteSpace(dir))
            dir = "backups";
        Directory.CreateDirectory(dir);

        var now = DateTime.UtcNow;

        var foods = await db.Foods
            .Select(f => new
            {
                f.Id, f.Name, f.NormalizedName, f.DefaultUoM,
                f.CaloriesPerUnit, f.ProteinPerUnit, f.CarbsPerUnit, f.FatPerUnit,
                f.Barcode, f.IconRef, f.CreatedAtUtc, f.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        var foodEntries = await db.FoodEntries
            .Select(e => new
            {
                e.Id, e.UserId, e.FoodId, e.FoodName,
                e.IntakeAtUtc, e.MealType, e.Quantity, e.UoM,
                e.Calories, e.Protein, e.Carbs, e.Fat,
                e.Source, e.AiConfidence,
            })
            .ToListAsync(ct);

        var foodIngredients = await db.FoodIngredients
            .Select(fi => new
            {
                fi.Id, fi.ParentFoodId, fi.ChildFoodId,
                fi.Quantity, fi.UoM,
            })
            .ToListAsync(ct);

        var snapshot = new
        {
            kind = "dedup-snapshot",
            schemaVersion = 1,
            exportedAt = now,
            note = "Pre-duplicate-cleanup snapshot — rollback path for OFB-43c.",
            foodCount = foods.Count,
            foodEntryCount = foodEntries.Count,
            foodIngredientCount = foodIngredients.Count,
            foods,
            foodEntries,
            foodIngredients,
        };

        var fileName = $"fuel-dedup-snapshot-{now:yyyyMMdd-HHmmss}.json";
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOptions), ct);

        logger.LogInformation("Dedup snapshot written: {Path} ({Foods} foods, {Entries} entries, {Ingredients} ingredients).",
            path, foods.Count, foodEntries.Count, foodIngredients.Count);

        return path;
    }
}
