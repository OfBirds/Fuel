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
            // Drop the non-unique index created in 20260714000001.
            // Pre-condition: duplicate foods have been cleaned up by
            // FoodDedupService, so no two rows share the same NormalizedName.
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
