using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.PersistenceConverterTests;

using Microsoft.EntityFrameworkCore;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// The conventional EF model and the separately published compiled model run the
/// same fixed converter contract. These cases do not qualify EF native queries.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class PersistedValueConverterParityTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public void ConventionalModelHasExactlyTheClosedConvertedPropertySet()
    {
        using var context = CreateContext(fixture.ConnectionString);
        PersistedValueConverterContract.AssertInventory(context.Model);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    public async Task ActualConverterDelegatesPreservePostgreSqlScalarsAndNulls(int descriptorIndex)
    {
        var schema = NewSchema();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var context = CreateContext(fixture.ConnectionString);
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(deadline.Token);
            await CreateSchemaAsync(connection, schema, deadline.Token);
            var descriptor = PersistedValueConverterContract.Descriptors[descriptorIndex];
            var checks = await PersistedValueConverterContract.ExercisePostgresAsync(
                connection, schema, context.Model, [descriptor], deadline.Token);
            Assert.Equal(descriptor.ParameterType == NpgsqlDbType.Jsonb ? 5 : 4, checks);
        }
        finally { await PersistedValueConverterContract.CleanupAsync(fixture.ConnectionString, schema); }
    }

    [Fact]
    public async Task ProviderSettingsInPlaceMutationUsesAnIndependentOriginalSnapshotAndPersists()
    {
        var schema = NewSchema();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CreateProviderTablesAsync(schema);
            await using var context = CreateContext(WithSearchPath(schema));
            var bag = new Dictionary<string, string> { ["region"] = "west", ["unicode"] = "雪 🔎" };
            var provider = new Provider
            {
                ProviderName = "converter tracking", ProviderType = ProviderType.OpenAI,
                Settings = bag, CreatedAt = Utc(), UpdatedAt = Utc()
            };
            context.Providers.Add(provider);
            await context.SaveChangesAsync(deadline.Token);
            var property = context.Entry(provider).Property(item => item.Settings);
            var snapshot = Assert.IsType<Dictionary<string, string>>(property.OriginalValue);
            Assert.NotSame(bag, snapshot);
            bag["region"] = "east";
            bag["added"] = "café";
            Assert.Equal("west", snapshot["region"]);
            Assert.False(snapshot.ContainsKey("added"));
            context.ChangeTracker.DetectChanges();
            Assert.True(property.IsModified);
            await context.SaveChangesAsync(deadline.Token);
            await AssertPersistedBagAsync(schema, "Providers", "Settings", provider.Id);
            context.ChangeTracker.Clear();
            var loaded = await context.Providers.AsNoTracking().SingleAsync(item => item.Id == provider.Id, deadline.Token);
            Assert.NotSame(bag, loaded.Settings);
            Assert.Equal("east", loaded.Settings!["region"]);
            loaded.Settings["region"] = "detached change";
            await AssertPersistedBagAsync(schema, "Providers", "Settings", provider.Id);
        }
        finally { await PersistedValueConverterContract.CleanupAsync(fixture.ConnectionString, schema); }
    }

    [Fact]
    public async Task CredentialSecretSettingsInPlaceMutationUsesAnIndependentOriginalSnapshotAndPersists()
    {
        var schema = NewSchema();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CreateProviderTablesAsync(schema);
            await using var context = CreateContext(WithSearchPath(schema));
            var provider = new Provider
            {
                ProviderName = "credential converter tracking", ProviderType = ProviderType.OpenAI,
                CreatedAt = Utc(), UpdatedAt = Utc()
            };
            var bag = new Dictionary<string, string> { ["region"] = "west", ["unicode"] = "雪 🔎" };
            var credential = new ProviderKeyCredential
            {
                Provider = provider, SecretSettings = bag, ApiKey = "enc:opaque-fixture",
                CreatedAt = Utc(), UpdatedAt = Utc()
            };
            context.ProviderKeyCredentials.Add(credential);
            await context.SaveChangesAsync(deadline.Token);
            var property = context.Entry(credential).Property(item => item.SecretSettings);
            var snapshot = Assert.IsType<Dictionary<string, string>>(property.OriginalValue);
            Assert.NotSame(bag, snapshot);
            bag["region"] = "east";
            bag["added"] = "café";
            Assert.Equal("west", snapshot["region"]);
            Assert.False(snapshot.ContainsKey("added"));
            context.ChangeTracker.DetectChanges();
            Assert.True(property.IsModified);
            await context.SaveChangesAsync(deadline.Token);
            await AssertPersistedBagAsync(schema, "ProviderKeyCredentials", "SecretSettings", credential.Id);
            context.ChangeTracker.Clear();
            var loaded = await context.ProviderKeyCredentials.AsNoTracking().SingleAsync(item => item.Id == credential.Id, deadline.Token);
            Assert.NotSame(bag, loaded.SecretSettings);
            Assert.Equal("east", loaded.SecretSettings!["region"]);
            loaded.SecretSettings["region"] = "detached change";
            await AssertPersistedBagAsync(schema, "ProviderKeyCredentials", "SecretSettings", credential.Id);
        }
        finally { await PersistedValueConverterContract.CleanupAsync(fixture.ConnectionString, schema); }
    }

    private async Task AssertPersistedBagAsync(string schema, string table, string column, int id)
    {
        PersistedValueConverterContract.ValidateSchema(schema);
        Assert.True((table == "Providers" && column == "Settings") ||
            (table == "ProviderKeyCredentials" && column == "SecretSettings"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = new NpgsqlCommand($"""
            SELECT "{column}"->>'region', "{column}"->>'unicode', "{column}"->>'added',
                   (SELECT count(*) FROM jsonb_object_keys("{column}"))
            FROM "{schema}"."{table}" WHERE "Id" = @id
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.Equal("east", reader.GetString(0));
        Assert.Equal("雪 🔎", reader.GetString(1));
        Assert.Equal("café", reader.GetString(2));
        Assert.Equal(3L, reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(deadline.Token));
    }

    private async Task CreateProviderTablesAsync(string schema)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await CreateSchemaAsync(connection, schema, deadline.Token);
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE "{schema}"."Providers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ProviderType" integer NOT NULL, "ProviderName" varchar(100) NOT NULL,
                "BaseUrl" text NULL, "Settings" jsonb NULL, "IsEnabled" boolean NOT NULL,
                "TrustProviderReportedCosts" boolean NOT NULL,
                "ProviderCostMarkupMultiplier" numeric NOT NULL,
                "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
            CREATE TABLE "{schema}"."ProviderKeyCredentials" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ProviderId" integer NOT NULL REFERENCES "{schema}"."Providers"("Id"),
                "ProviderAccountGroup" smallint NOT NULL, "ApiKey" text NULL, "BaseUrl" text NULL,
                "SecretSettings" jsonb NULL, "KeyName" text NULL, "IsPrimary" boolean NOT NULL,
                "IsEnabled" boolean NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL)
            """, connection);
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private static async Task CreateSchemaAsync(NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        PersistedValueConverterContract.ValidateSchema(schema);
        await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ConduitDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options);

    private string WithSearchPath(string schema) =>
        new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = schema, Pooling = false, Timeout = 10, CommandTimeout = 10, CancellationTimeout = 1000
        }.ConnectionString;

    private static string NewSchema() => PersistedValueConverterContract.SchemaPrefix + Guid.NewGuid().ToString("N");
    private static DateTime Utc() => new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
}
