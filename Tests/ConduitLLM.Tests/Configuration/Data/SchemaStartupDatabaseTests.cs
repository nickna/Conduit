using ConduitLLM.Configuration.Data;
using ConduitLLM.Migrator;
using ConduitLLM.SchemaStartupFixtures;

using Npgsql;

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
        var variables = new[] { "DATABASE_URL", "ConduitLLM__Messaging__Backend", "ConduitLLM__Messaging__Wolverine__Transport" };
        var originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Assert.NotNull(fixture.AppliedVersion);
            Assert.NotEqual(ConduitSchemaVersion.Current, fixture.AppliedVersion);
            var previousHistory = await ReadAppliedMigrationsAsync(fixture.ConnectionString);
            Assert.NotEmpty(previousHistory);
            Assert.Equal(fixture.AppliedVersion, previousHistory[^1]);
            Assert.DoesNotContain(ConduitSchemaVersion.Current, previousHistory);
            // The fixture applies the immediately preceding migration's real DDL.
            // Shadow-relationship upgrades have their own explicitly targeted tests;
            // this release adds durable function claims to the already paired schema.
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "WebhookReplayAudits"));
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "VirtualKeyGroupTransactions"));
            Assert.False(await TableExistsAsync(fixture.ConnectionString, "FunctionExecutionClaims"));
            Environment.SetEnvironmentVariable(variables[0], fixture.ConnectionString);
            Environment.SetEnvironmentVariable(variables[1], "Wolverine");
            Environment.SetEnvironmentVariable(variables[2], "InMemory");
            Assert.Equal(0, await MigrationRunner.RunAsync());
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            Assert.True((await new SchemaVersionProbe(dataSource).GetStatusAsync()).IsCurrent);
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "WebhookReplayAudits"));
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "VirtualKeyGroupTransactions"));
            Assert.True(await TableExistsAsync(fixture.ConnectionString, "FunctionExecutionClaims"));
            var upgradedHistory = await ReadAppliedMigrationsAsync(fixture.ConnectionString);
            Assert.Equal(previousHistory.Append(ConduitSchemaVersion.Current), upgradedHistory);
            Assert.Equal(0, await MigrationRunner.RunAsync());
            Assert.Equal(upgradedHistory, await ReadAppliedMigrationsAsync(fixture.ConnectionString));
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

    private static async Task<List<string>> ReadAppliedMigrationsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"
            """, connection);
        await using var reader = await query.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }
}
