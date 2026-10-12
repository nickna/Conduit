using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class AddExactOriginalBalanceAdjustmentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OriginalAmount",
                table: "VirtualKeyGroupTransactions",
                type: "numeric(20,8)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalAmountEvidence",
                table: "VirtualKeyGroupTransactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OriginalAmountVerifiedAtUtc",
                table: "VirtualKeyGroupTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalAmountVerifiedBy",
                table: "VirtualKeyGroupTransactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalAmount",
                table: "VirtualKeyGroupTransactions");

            migrationBuilder.DropColumn(
                name: "OriginalAmountEvidence",
                table: "VirtualKeyGroupTransactions");

            migrationBuilder.DropColumn(
                name: "OriginalAmountVerifiedAtUtc",
                table: "VirtualKeyGroupTransactions");

            migrationBuilder.DropColumn(
                name: "OriginalAmountVerifiedBy",
                table: "VirtualKeyGroupTransactions");
        }
    }
}
