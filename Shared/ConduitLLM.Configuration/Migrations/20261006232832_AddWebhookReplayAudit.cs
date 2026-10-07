using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookReplayAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookReplayAudits",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Cycle = table.Column<int>(type: "integer", nullable: false),
                    PreviousAttempts = table.Column<int>(type: "integer", nullable: false),
                    DeadLetterId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookReplayAudits", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookReplayAudits_DeliveryId_Cycle",
                table: "WebhookReplayAudits",
                columns: new[] { "DeliveryId", "Cycle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookReplayAudits_RetainUntil",
                table: "WebhookReplayAudits",
                column: "RetainUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookReplayAudits");
        }
    }
}
