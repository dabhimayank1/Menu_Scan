using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MenuScan.Migrations
{
    /// <inheritdoc />
    public partial class Phase5FilteredUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiningTables_RestaurantId_TableNumber",
                table: "DiningTables");

            migrationBuilder.DropIndex(
                name: "IX_Categories_RestaurantId_Name",
                table: "Categories");

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_RestaurantId_TableNumber",
                table: "DiningTables",
                columns: new[] { "RestaurantId", "TableNumber" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_RestaurantId_Name",
                table: "Categories",
                columns: new[] { "RestaurantId", "Name" },
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiningTables_RestaurantId_TableNumber",
                table: "DiningTables");

            migrationBuilder.DropIndex(
                name: "IX_Categories_RestaurantId_Name",
                table: "Categories");

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_RestaurantId_TableNumber",
                table: "DiningTables",
                columns: new[] { "RestaurantId", "TableNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_RestaurantId_Name",
                table: "Categories",
                columns: new[] { "RestaurantId", "Name" },
                unique: true);
        }
    }
}
