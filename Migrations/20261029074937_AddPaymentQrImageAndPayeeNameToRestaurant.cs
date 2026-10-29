using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MenuScan.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentQrImageAndPayeeNameToRestaurant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayeeName",
                table: "Restaurants",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentQrImageUrl",
                table: "Restaurants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayeeName",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "PaymentQrImageUrl",
                table: "Restaurants");
        }
    }
}
