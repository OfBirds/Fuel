using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodNormalizedName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Non-nullable with empty default so the DDL succeeds on tables with rows.
            // The backfill immediately overwrites every row with the computed value.
            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Foods",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill: identical logic to FoodNameNormalizer.Normalize:
            //   trim → lowercase → strip parenthesized suffix → collapse whitespace → trim.
            // SPLIT_PART("Name", '(', 1) strips everything from the first '(' onward.
            // regexp_replace(..., '\s+', ' ', 'g') collapses internal whitespace runs.
            // lower(trim(...)) normalises case + leading/trailing whitespace.
            migrationBuilder.Sql(
                @"UPDATE ""Foods""
                  SET ""NormalizedName"" = TRIM(LOWER(
                        REGEXP_REPLACE(
                            TRIM(SPLIT_PART(""Name"", '(', 1)),
                            '\s+', ' ', 'g'
                        )
                      ));");

            // Non-unique index — unique constraint deferred to OFB-43c cleanup.
            migrationBuilder.CreateIndex(
                name: "IX_Foods_NormalizedName",
                table: "Foods",
                column: "NormalizedName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Foods_NormalizedName",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Foods");
        }
    }
}
