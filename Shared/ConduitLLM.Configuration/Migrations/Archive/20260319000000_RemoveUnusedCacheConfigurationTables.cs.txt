using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedCacheConfigurationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CacheConfigurationAudits");

            migrationBuilder.DropTable(
                name: "CacheConfigurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CacheConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Region = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultTtlSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxTtlSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxEntries = table.Column<int>(type: "integer", nullable: false),
                    MaxMemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    EvictionPolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UseMemoryCache = table.Column<bool>(type: "boolean", nullable: false),
                    UseDistributedCache = table.Column<bool>(type: "boolean", nullable: false),
                    EnableCompression = table.Column<bool>(type: "boolean", nullable: false),
                    CompressionThresholdBytes = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExtendedConfig = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CacheConfigurationAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Region = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OldConfigJson = table.Column<string>(type: "text", nullable: true),
                    NewConfigJson = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangeSource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheConfigurationAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurationAudits_ChangedAt",
                table: "CacheConfigurationAudits",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurationAudits_ChangedBy",
                table: "CacheConfigurationAudits",
                column: "ChangedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurationAudits_Region",
                table: "CacheConfigurationAudits",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurationAudits_Region_ChangedAt",
                table: "CacheConfigurationAudits",
                columns: new[] { "Region", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurations_Region",
                table: "CacheConfigurations",
                column: "Region",
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurations_Region_IsActive",
                table: "CacheConfigurations",
                columns: new[] { "Region", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheConfigurations_UpdatedAt",
                table: "CacheConfigurations",
                column: "UpdatedAt");
        }
    }
}
