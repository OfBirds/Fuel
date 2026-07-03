using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodCatalogueAndEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DailyCalorieGoal",
                table: "Users",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Foods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DefaultUoM = table.Column<string>(type: "text", nullable: false),
                    CaloriesPerUnit = table.Column<double>(type: "double precision", nullable: false),
                    ProteinPerUnit = table.Column<double>(type: "double precision", nullable: true),
                    CarbsPerUnit = table.Column<double>(type: "double precision", nullable: true),
                    FatPerUnit = table.Column<double>(type: "double precision", nullable: true),
                    IconRef = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Foods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FoodEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FoodId = table.Column<Guid>(type: "uuid", nullable: true),
                    FoodName = table.Column<string>(type: "text", nullable: false),
                    IntakeAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MealType = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<double>(type: "double precision", nullable: false),
                    UoM = table.Column<string>(type: "text", nullable: false),
                    Calories = table.Column<double>(type: "double precision", nullable: false),
                    Protein = table.Column<double>(type: "double precision", nullable: true),
                    Carbs = table.Column<double>(type: "double precision", nullable: true),
                    Fat = table.Column<double>(type: "double precision", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false, defaultValue: "Manual"),
                    AiConfidence = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FoodEntries_Foods_FoodId",
                        column: x => x.FoodId,
                        principalTable: "Foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FoodEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FoodIngredients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentFoodId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildFoodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<double>(type: "double precision", nullable: false),
                    UoM = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodIngredients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FoodIngredients_Foods_ChildFoodId",
                        column: x => x.ChildFoodId,
                        principalTable: "Foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FoodIngredients_Foods_ParentFoodId",
                        column: x => x.ParentFoodId,
                        principalTable: "Foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoodEntries_FoodId",
                table: "FoodEntries",
                column: "FoodId");

            migrationBuilder.CreateIndex(
                name: "IX_FoodEntries_UserId_IntakeAtUtc",
                table: "FoodEntries",
                columns: new[] { "UserId", "IntakeAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FoodIngredients_ChildFoodId",
                table: "FoodIngredients",
                column: "ChildFoodId");

            migrationBuilder.CreateIndex(
                name: "IX_FoodIngredients_ParentFoodId",
                table: "FoodIngredients",
                column: "ParentFoodId");

            migrationBuilder.CreateIndex(
                name: "IX_Foods_Name",
                table: "Foods",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FoodEntries");

            migrationBuilder.DropTable(
                name: "FoodIngredients");

            migrationBuilder.DropTable(
                name: "Foods");

            migrationBuilder.DropColumn(
                name: "DailyCalorieGoal",
                table: "Users");
        }
    }
}
