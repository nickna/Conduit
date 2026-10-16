using ConduitLLM.Configuration.Data;
using ConduitLLM.Migrator;
using ConduitLLM.SchemaStartupFixtures;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Tests.Configuration.Data;

[Collection("MigrationEnvironment")]
public sealed class SchemaStartupDatabaseTests
{
    [SkippableFact]
    public async Task MissingSchema_IsIsolatedUninitializedAndCleanupIsIdempotent()
    {
        var management = ManagementConnection();
        var fixture = await SchemaStartupDatabase.CreateAsync(management, previousSchema: false);
        try
        {
            Assert.Null(fixture.AppliedVersion);
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            Assert.False((await new SchemaVersionProbe(dataSource).GetStatusAsync()).IsCurrent);
            Assert.False(await TableExistsAsync(fixture.ConnectionString, "__EFMigrationsHistory"));
            Assert.True(await DatabaseExistsAsync(management, fixture.Name));
        }
        finally { await DropAsync(management, fixture.Name); }
        Assert.False(await DatabaseExistsAsync(management, fixture.Name));
        await DropAsync(management, fixture.Name);
    }

    [SkippableFact]
    public async Task PreviousSchema_IsRealPriorDdlAndReleaseMigratorUpgradesIt()
    {
        var management = ManagementConnection();
        var fixture = await SchemaStartupDatabase.CreateAsync(management, previousSchema: true);
        var removedShadowColumns = new[]
        {
            (Table: "FunctionCostMappings", Column: "FunctionCostId1"),
            (Table: "FunctionExecutionAudits", Column: "FunctionExecutionId1")
        };
        var variables = new[] { "DATABASE_URL", "ConduitLLM__Messaging__Backend", "ConduitLLM__Messaging__Wolverine__Transport" };
        var originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Assert.NotNull(fixture.AppliedVersion);
            Assert.NotEqual(ConduitSchemaVersion.Current, fixture.AppliedVersion);
            // The actual prior migration retains both accidental shadow relationships.
            // The latest migration removes them while keeping the canonical FKs.
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "WebhookReplayAudits"));
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "VirtualKeyGroupTransactions"));
            foreach (var (table, column) in removedShadowColumns)
            {
                var previousColumns = await ReadColumnNullabilityAsync(fixture.ConnectionString, table, new[] { column });
                Assert.True(previousColumns.TryGetValue(column, out var nullable) && nullable,
                    $"The previous migration must retain nullable shadow column '{table}.{column}'.");
            }
            Environment.SetEnvironmentVariable(variables[0], fixture.ConnectionString);
            Environment.SetEnvironmentVariable(variables[1], "Wolverine");
            Environment.SetEnvironmentVariable(variables[2], "InMemory");
            Assert.Equal(0, await MigrationRunner.RunAsync());
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            Assert.True((await new SchemaVersionProbe(dataSource).GetStatusAsync()).IsCurrent);
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "WebhookReplayAudits"));
            foreach (var (table, column) in removedShadowColumns)
                Assert.Empty(await ReadColumnNullabilityAsync(fixture.ConnectionString, table, new[] { column }));
        }
        finally
        {
            for (var index = 0; index < variables.Length; index++)
                Environment.SetEnvironmentVariable(variables[index], originals[index]);
            await DropAsync(management, fixture.Name);
        }
    }

    [SkippableFact]
    public async Task MatchingNameWithoutOwnershipMarker_CannotBeRemoved()
    {
        var management = ManagementConnection();
        var name = "conduit_native_startup_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(management) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaStartupDatabase.DropAsync(management, name));
            Assert.Contains("ownership marker", exception.Message);
            Assert.True(await DatabaseExistsAsync(management, name));
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(deadline.Token);
        }
    }

    [Theory]
    [InlineData("conduitdb")]
    [InlineData("conduit_native_startup_00000000000000000000000000000000\";")]
    [InlineData("conduit_native_startup_gggggggggggggggggggggggggggggggg")]
    public async Task UnsafeDatabaseNames_AreRejectedBeforeConnecting(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SchemaStartupDatabase.DropAsync("Host=127.0.0.1;Port=1", name));
    }

    private static string ManagementConnection()
    {
        var connection = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connection), "Real PostgreSQL tests require CONDUIT_LOCK_TEST_POSTGRES with database creation rights.");
        return connection!;
    }

    private static async Task DropAsync(string management, string name)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await SchemaStartupDatabase.DropAsync(management, name, deadline.Token);
    }

    private static async Task<bool> DatabaseExistsAsync(string management, string name)
    {
        await using var connection = new NpgsqlConnection(management);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", connection);
        query.Parameters.AddWithValue("name", name);
        return (bool)(await query.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string name)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name)", connection);
        query.Parameters.AddWithValue("name", name);
        return (bool)(await query.ExecuteScalarAsync())!;
    }

    private static async Task<Dictionary<string, bool>> ReadColumnNullabilityAsync(
        string connectionString, string table, IReadOnlyList<string> columns)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT column_name, is_nullable = 'YES'
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = ANY(@columns)
            """, connection);
        query.Parameters.Add("table", NpgsqlDbType.Text).Value = table;
        query.Parameters.Add("columns", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = columns.ToArray();
        await using var reader = await query.ExecuteReaderAsync();
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0), reader.GetBoolean(1));
        return result;
    }
}
