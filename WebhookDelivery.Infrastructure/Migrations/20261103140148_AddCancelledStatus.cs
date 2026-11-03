using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebhookDelivery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCancelledStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_deliveries_attempt_count",
                table: "deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_deliveries_status",
                table: "deliveries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_deliveries_status",
                table: "deliveries",
                sql: "status IN ('pending','delivered','failed','cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_deliveries_status",
                table: "deliveries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_deliveries_attempt_count",
                table: "deliveries",
                sql: "attempt_count >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_deliveries_status",
                table: "deliveries",
                sql: "status IN ('pending','delivered','failed')");
        }
    }
}
