using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Migrations
{
    /// <inheritdoc />
    public partial class MakeFoodNormalizedNameUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dedupe in SQL before creating the unique index. FoodDedupService
            // can't have run on a database that jumps straight to this version
            // (migrations apply before the new app starts), so the migration
            // must be self-sufficient. Same survivor priority as the service:
            // composite > barcode > oldest CreatedAtUtc > lowest Id.
            // No-op when there are no duplicates. InMemory (unit tests) never
            // runs migrations, so the raw SQL is Postgres-only by construction.
            migrationBuilder.Sql(@"
CREATE TEMP TABLE ef_dedup_map AS
WITH dup_names AS (
    SELECT ""NormalizedName""
    FROM ""Foods""
    WHERE ""NormalizedName"" <> ''
    GROUP BY ""NormalizedName""
    HAVING COUNT(*) > 1
),
candidates AS (
    SELECT f.""Id"", f.""NormalizedName"", f.""CreatedAtUtc"",
           (f.""Barcode"" IS NOT NULL) AS has_barcode,
           EXISTS (SELECT 1 FROM ""FoodIngredients"" fi
                   WHERE fi.""ParentFoodId"" = f.""Id"") AS is_composite
    FROM ""Foods"" f
    JOIN dup_names d USING (""NormalizedName"")
),
ranked AS (
    SELECT ""Id"",
           first_value(""Id"") OVER (
               PARTITION BY ""NormalizedName""
               ORDER BY is_composite DESC, has_barcode DESC,
                        ""CreatedAtUtc"" ASC, ""Id"" ASC
           ) AS survivor_id
    FROM candidates
)
SELECT ""Id"" AS dup_id, survivor_id
FROM ranked
WHERE ""Id"" <> survivor_id;

UPDATE ""FoodEntries"" e SET ""FoodId"" = m.survivor_id
FROM ef_dedup_map m WHERE e.""FoodId"" = m.dup_id;

UPDATE ""FoodIngredients"" fi SET ""ChildFoodId"" = m.survivor_id
FROM ef_dedup_map m WHERE fi.""ChildFoodId"" = m.dup_id;

UPDATE ""FoodIngredients"" fi SET ""ParentFoodId"" = m.survivor_id
FROM ef_dedup_map m WHERE fi.""ParentFoodId"" = m.dup_id;

DELETE FROM ""FoodIngredients"" WHERE ""ParentFoodId"" = ""ChildFoodId"";

DELETE FROM ""FoodIngredients"" a
USING ""FoodIngredients"" b
WHERE a.""ParentFoodId"" = b.""ParentFoodId""
  AND a.""ChildFoodId"" = b.""ChildFoodId""
  AND a.""Id"" > b.""Id"";

UPDATE ""UserFoodPriorities"" sp SET ""Ponder"" = q.min_ponder
FROM (
    SELECT p.""UserId"", m.survivor_id, MIN(p.""Ponder"") AS min_ponder
    FROM ""UserFoodPriorities"" p
    JOIN ef_dedup_map m ON p.""FoodId"" = m.dup_id
    GROUP BY p.""UserId"", m.survivor_id
) q
WHERE sp.""UserId"" = q.""UserId"" AND sp.""FoodId"" = q.survivor_id
  AND sp.""Ponder"" > q.min_ponder;

INSERT INTO ""UserFoodPriorities"" (""UserId"", ""FoodId"", ""Ponder"")
SELECT p.""UserId"", m.survivor_id, MIN(p.""Ponder"")
FROM ""UserFoodPriorities"" p
JOIN ef_dedup_map m ON p.""FoodId"" = m.dup_id
GROUP BY p.""UserId"", m.survivor_id
ON CONFLICT (""UserId"", ""FoodId"") DO NOTHING;

DELETE FROM ""UserFoodPriorities"" p
USING ef_dedup_map m WHERE p.""FoodId"" = m.dup_id;

DELETE FROM ""Foods"" f
USING ef_dedup_map m WHERE f.""Id"" = m.dup_id;

DROP TABLE ef_dedup_map;
");

            // Drop the non-unique index created in 20260714000001.
            migrationBuilder.DropIndex(
                name: "IX_Foods_NormalizedName",
                table: "Foods");

            // Recreate as unique — this is the final step of OFB-43c.
            // From here on, the application layer (get-or-create) and this
            // constraint both prevent duplicate NormalizedName entries.
            migrationBuilder.CreateIndex(
                name: "IX_Foods_NormalizedName",
                table: "Foods",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to non-unique — only useful if rolling back past OFB-43c.
            migrationBuilder.DropIndex(
                name: "IX_Foods_NormalizedName",
                table: "Foods");

            migrationBuilder.CreateIndex(
                name: "IX_Foods_NormalizedName",
                table: "Foods",
                column: "NormalizedName");
        }
    }
}
