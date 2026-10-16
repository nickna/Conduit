using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class PairFunctionInverseRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep validation and removal atomic even while older application instances
            // still write either relationship. Never choose between conflicting parents.
            migrationBuilder.Sql("""
                LOCK TABLE "FunctionCostMappings", "FunctionExecutionAudits" IN ACCESS EXCLUSIVE MODE;
                DO $function_relationships$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "FunctionCostMappings"
                        WHERE "FunctionCostId1" IS NOT NULL
                            AND "FunctionCostId1" IS DISTINCT FROM "FunctionCostId"
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            MESSAGE = 'Cannot pair function inverse relationships: FunctionCostMappings.FunctionCostId1 conflicts with FunctionCostId.',
                            HINT = 'Reconcile the conflicting parent references before retrying. See docs/operations/function-inverse-relationship-migration.md.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "FunctionExecutionAudits"
                        WHERE "FunctionExecutionId1" IS NOT NULL
                            AND "FunctionExecutionId1" IS DISTINCT FROM "FunctionExecutionId"
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            MESSAGE = 'Cannot pair function inverse relationships: FunctionExecutionAudits.FunctionExecutionId1 conflicts with FunctionExecutionId.',
                            HINT = 'Reconcile the conflicting parent references before retrying. See docs/operations/function-inverse-relationship-migration.md.';
                    END IF;
                END;
                $function_relationships$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_FunctionCostMappings_FunctionCosts_FunctionCostId1",
                table: "FunctionCostMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_FunctionExecutionAudits_FunctionExecutions_FunctionExecuti~1",
                table: "FunctionExecutionAudits");

            migrationBuilder.DropIndex(
                name: "IX_FunctionExecutionAudits_FunctionExecutionId1",
                table: "FunctionExecutionAudits");

            migrationBuilder.DropIndex(
                name: "IX_FunctionCostMappings_FunctionCostId1",
                table: "FunctionCostMappings");

            migrationBuilder.DropColumn(
                name: "FunctionExecutionId1",
                table: "FunctionExecutionAudits");

            migrationBuilder.DropColumn(
                name: "FunctionCostId1",
                table: "FunctionCostMappings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "FunctionCostMappings", "FunctionExecutionAudits" IN ACCESS EXCLUSIVE MODE;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "FunctionExecutionId1",
                table: "FunctionExecutionAudits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FunctionCostId1",
                table: "FunctionCostMappings",
                type: "integer",
                nullable: true);

            // Restore the old inverse collections for every existing child, including
            // rows created after this migration was applied.
            migrationBuilder.Sql("""
                UPDATE "FunctionCostMappings" SET "FunctionCostId1" = "FunctionCostId";
                UPDATE "FunctionExecutionAudits" SET "FunctionExecutionId1" = "FunctionExecutionId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FunctionExecutionAudits_FunctionExecutionId1",
                table: "FunctionExecutionAudits",
                column: "FunctionExecutionId1");

            migrationBuilder.CreateIndex(
                name: "IX_FunctionCostMappings_FunctionCostId1",
                table: "FunctionCostMappings",
                column: "FunctionCostId1");

            migrationBuilder.AddForeignKey(
                name: "FK_FunctionCostMappings_FunctionCosts_FunctionCostId1",
                table: "FunctionCostMappings",
                column: "FunctionCostId1",
                principalTable: "FunctionCosts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_FunctionExecutionAudits_FunctionExecutions_FunctionExecuti~1",
                table: "FunctionExecutionAudits",
                column: "FunctionExecutionId1",
                principalTable: "FunctionExecutions",
                principalColumn: "Id");
        }
    }
}
