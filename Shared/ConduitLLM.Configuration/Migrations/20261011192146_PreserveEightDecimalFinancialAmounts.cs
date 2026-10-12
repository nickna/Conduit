using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class PreserveEightDecimalFinancialAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Increase both precision and scale to preserve the existing integral range.
            // Historical six-digit rounding is irreversible; do not backfill amounts.
            // Reconciliation policy: docs/billing-alerting.md.
            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "VirtualKeySpendHistory",
                type: "numeric(12,8)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "BalanceAfter",
                table: "VirtualKeyGroupTransactions",
                type: "numeric(20,8)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "VirtualKeyGroupTransactions",
                type: "numeric(20,8)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Cost",
                table: "RequestLogs",
                type: "numeric(12,8)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "CalculatedCost",
                table: "PricingAuditEvents",
                type: "numeric(12,8)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ToolUsageCost",
                table: "BillingAuditEvents",
                type: "numeric(12,8)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,6)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CalculatedCost",
                table: "BillingAuditEvents",
                type: "numeric(12,8)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,6)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing rounds newly written amounts. A code rollback should retain
            // the expanded schema rather than applying this lossy schema downgrade.
            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "VirtualKeySpendHistory",
                type: "numeric(10,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,8)");

            migrationBuilder.AlterColumn<decimal>(
                name: "BalanceAfter",
                table: "VirtualKeyGroupTransactions",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(20,8)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "VirtualKeyGroupTransactions",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(20,8)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Cost",
                table: "RequestLogs",
                type: "numeric(10,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,8)");

            migrationBuilder.AlterColumn<decimal>(
                name: "CalculatedCost",
                table: "PricingAuditEvents",
                type: "numeric(10,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,8)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ToolUsageCost",
                table: "BillingAuditEvents",
                type: "numeric(10,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,8)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CalculatedCost",
                table: "BillingAuditEvents",
                type: "numeric(10,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,8)",
                oldNullable: true);
        }
    }
}
