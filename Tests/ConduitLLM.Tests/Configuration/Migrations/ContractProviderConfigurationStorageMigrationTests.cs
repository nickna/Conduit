using System.Reflection;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Migrations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ConduitLLM.Tests.Configuration.Migrations;

public sealed class ContractProviderConfigurationStorageMigrationTests
{
    [Fact]
    public void Migration_HasDiscoverableMetadataAndContractedTargetModel()
    {
        var migrationType = typeof(ContractProviderConfigurationStorage);

        var migrationAttribute = migrationType.GetCustomAttribute<MigrationAttribute>();
        Assert.NotNull(migrationAttribute);
        Assert.Equal("20260725232503_ContractProviderConfigurationStorage", migrationAttribute.Id);

        var dbContextAttribute = migrationType.GetCustomAttribute<DbContextAttribute>();
        Assert.NotNull(dbContextAttribute);
        Assert.Equal(typeof(ConduitDbContext), dbContextAttribute.ContextType);

        var targetModel = new ContractProviderConfigurationStorage().TargetModel;
        var keyCredential = targetModel.FindEntityType(typeof(ProviderKeyCredential));

        Assert.NotNull(keyCredential);
        Assert.Null(keyCredential.FindProperty("Organization"));
    }

    [Fact]
    public void LatestMigration_TargetModelMatchesTheCurrentContextModel()
    {
        using var context = CreateContext();

        var differ = context.GetService<IMigrationsModelDiffer>();
        var currentModel = context.GetService<IDesignTimeModel>().Model;
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var latestMigration = migrationsAssembly.Migrations
            .OrderBy(migration => migration.Key, StringComparer.Ordinal)
            .Last();
        var migration = migrationsAssembly.CreateMigration(latestMigration.Value, context.Database.ProviderName!);
        var targetModel = context.GetService<IModelRuntimeInitializer>()
            .Initialize(migration.TargetModel, designTime: true);

        Assert.False(differ.HasDifferences(
            targetModel.GetRelationalModel(),
            currentModel.GetRelationalModel()));
    }

    [Fact]
    public void Migration_GeneratesPostgresSqlFromTheExpandHead()
    {
        using var context = CreateContext();

        var script = context.GetService<IMigrator>().GenerateScript(
            "20260725105103_AddProviderKeySecretSettings",
            "20260725232503_ContractProviderConfigurationStorage");

        Assert.Contains("UPDATE \"Providers\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"ProviderKeyCredentials\" DROP COLUMN \"Organization\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Up_ClearsOnlyCanonicalCloudflareUrlShapesBeforeDroppingOrganization()
    {
        var operations = new TestableMigration().BuildUp();

        var cleanup = Assert.IsType<SqlOperation>(operations[0]);
        Assert.Contains("\"ProviderType\" = 12", cleanup.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Settings\" ->> 'account_id'", cleanup.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "^https://api[.]cloudflare[.]com/client/v4/accounts/[^/]+/ai/v1$",
            cleanup.Sql,
            StringComparison.Ordinal);

        var drop = Assert.IsType<DropColumnOperation>(operations[1]);
        Assert.Equal("ProviderKeyCredentials", drop.Table);
        Assert.Equal("Organization", drop.Name);
    }

    [Fact]
    public void Down_RestoresCompatibilityStorageFromCanonicalProviderSettings()
    {
        var operations = new TestableMigration().BuildDown();

        var add = Assert.IsType<AddColumnOperation>(operations[0]);
        Assert.Equal("ProviderKeyCredentials", add.Table);
        Assert.Equal("Organization", add.Name);
        Assert.True(add.IsNullable);

        var restore = Assert.IsType<SqlOperation>(operations[1]);
        Assert.Contains(
            "SET \"Organization\" = p.\"Settings\" ->> 'organization'",
            restore.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "'https://api.cloudflare.com/client/v4/accounts/'",
            restore.Sql,
            StringComparison.Ordinal);
    }

    private sealed class TestableMigration : ContractProviderConfigurationStorage
    {
        public IReadOnlyList<MigrationOperation> BuildUp()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Up(builder);
            return builder.Operations;
        }

        public IReadOnlyList<MigrationOperation> BuildDown()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Down(builder);
            return builder.Operations;
        }
    }

    private static ConduitDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql("Host=localhost;Database=conduit_model_check;Username=conduit")
            .Options;
        return new ConduitDbContext(options);
    }
}
