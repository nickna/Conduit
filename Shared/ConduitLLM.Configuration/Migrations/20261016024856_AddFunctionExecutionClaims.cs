using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class AddFunctionExecutionClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FunctionExecutionClaims",
                columns: table => new
                {
                    VirtualKeyId = table.Column<int>(type: "integer", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingGroupId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    BilledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionExecutionClaims", x => new { x.VirtualKeyId, x.KeyHash });
                    table.CheckConstraint("CK_FunctionExecutionClaims_Billed", "\"BilledAtUtc\" IS NULL OR \"State\" = 'completed'");
                    table.CheckConstraint("CK_FunctionExecutionClaims_Result", "(\"State\" = 'completed') = (\"ResultJson\" IS NOT NULL)");
                    table.CheckConstraint("CK_FunctionExecutionClaims_State", "\"State\" IN ('claimed', 'dispatching', 'completed', 'uncertain')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_FunctionExecutionClaims_ExecutionId",
                table: "FunctionExecutionClaims",
                column: "ExecutionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FunctionExecutionClaims");
        }
    }
}
