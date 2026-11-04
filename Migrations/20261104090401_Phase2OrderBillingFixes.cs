using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MenuScan.Migrations
{
    /// <inheritdoc />
    public partial class Phase2OrderBillingFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_IdempotencyKey",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_RestaurantId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_CustomerFeedbacks_OrderId",
                table: "CustomerFeedbacks");

            migrationBuilder.AddColumn<Guid>(
                name: "OrderId",
                table: "ServiceRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Bills",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_OrderId",
                table: "ServiceRequests",
                column: "OrderId");

            // Deduplicate existing duplicate (RestaurantId, IdempotencyKey) rows by suffixing duplicates before unique index
            migrationBuilder.Sql(@"
                WITH NumberedOrders AS (
                    SELECT Id, RestaurantId, IdempotencyKey,
                           ROW_NUMBER() OVER (PARTITION BY RestaurantId, IdempotencyKey ORDER BY CreatedAtUtc) AS rn
                    FROM Orders
                    WHERE IdempotencyKey IS NOT NULL AND IdempotencyKey <> ''
                )
                UPDATE o
                SET IdempotencyKey = SUBSTRING(o.IdempotencyKey + '_' + CAST(n.rn AS NVARCHAR(10)), 1, 100)
                FROM Orders o
                INNER JOIN NumberedOrders n ON o.Id = n.Id
                WHERE n.rn > 1;

                UPDATE Orders
                SET IdempotencyKey = 'IDEMP-' + CAST(Id AS NVARCHAR(36))
                WHERE IdempotencyKey IS NULL OR IdempotencyKey = '';
            ");

            // Deduplicate existing duplicate CustomerFeedbacks per OrderId before unique index
            migrationBuilder.Sql(@"
                WITH RankedFeedbacks AS (
                    SELECT Id, OrderId,
                           ROW_NUMBER() OVER (PARTITION BY OrderId ORDER BY CreatedAtUtc DESC, Id) AS rn
                    FROM CustomerFeedbacks
                )
                DELETE FROM CustomerFeedbacks
                WHERE Id IN (SELECT Id FROM RankedFeedbacks WHERE rn > 1);
            ");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RestaurantId_IdempotencyKey",
                table: "Orders",
                columns: new[] { "RestaurantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFeedbacks_OrderId",
                table: "CustomerFeedbacks",
                column: "OrderId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequests_Orders_OrderId",
                table: "ServiceRequests",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequests_Orders_OrderId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_OrderId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "IX_Orders_RestaurantId_IdempotencyKey",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_CustomerFeedbacks_OrderId",
                table: "CustomerFeedbacks");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Bills");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_IdempotencyKey",
                table: "Orders",
                column: "IdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RestaurantId",
                table: "Orders",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFeedbacks_OrderId",
                table: "CustomerFeedbacks",
                column: "OrderId");
        }
    }
}
