using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodBarcode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Barcode",
                table: "Foods",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Foods_Barcode",
                table: "Foods",
                column: "Barcode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Foods_Barcode",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "Barcode",
                table: "Foods");
        }
    }
}
