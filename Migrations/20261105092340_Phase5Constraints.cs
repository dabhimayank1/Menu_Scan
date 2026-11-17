using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MenuScan.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Constraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiningTables_RestaurantId",
                table: "DiningTables");

            migrationBuilder.DropIndex(
                name: "IX_Categories_RestaurantId",
                table: "Categories");

            // Deduplicate DiningTables by updating duplicates with suffix ' (2)', ' (3)', etc. Zero deletions!
            migrationBuilder.Sql(@"
                WHILE EXISTS (
                    SELECT 1 FROM DiningTables
                    GROUP BY RestaurantId, TableNumber
                    HAVING COUNT(*) > 1
                )
                BEGIN
                    ;WITH Dups AS (
                        SELECT Id, RestaurantId, TableNumber,
                               ROW_NUMBER() OVER (PARTITION BY RestaurantId, TableNumber ORDER BY Id) as rn
                        FROM DiningTables
                    )
                    UPDATE dt
                    SET TableNumber = SUBSTRING(dt.TableNumber + ' (' + CAST(d.rn AS NVARCHAR(10)) + ')', 1, 50)
                    FROM DiningTables dt
                    INNER JOIN Dups d ON dt.Id = d.Id
                    WHERE d.rn > 1;
                END
            ");

            // Deduplicate Categories by updating duplicates with suffix ' (2)', ' (3)', etc. Zero deletions!
            migrationBuilder.Sql(@"
                WHILE EXISTS (
                    SELECT 1 FROM Categories
                    GROUP BY RestaurantId, Name
                    HAVING COUNT(*) > 1
                )
                BEGIN
                    ;WITH Dups AS (
                        SELECT Id, RestaurantId, Name,
                               ROW_NUMBER() OVER (PARTITION BY RestaurantId, Name ORDER BY Id) as rn
                        FROM Categories
                    )
                    UPDATE c
                    SET Name = SUBSTRING(c.Name + ' (' + CAST(d.rn AS NVARCHAR(10)) + ')', 1, 100)
                    FROM Categories c
                    INNER JOIN Dups d ON c.Id = d.Id
                    WHERE d.rn > 1;
                END
            ");

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
                name: "IX_DiningTables_RestaurantId",
                table: "DiningTables",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_RestaurantId",
                table: "Categories",
                column: "RestaurantId");
        }
    }
}
