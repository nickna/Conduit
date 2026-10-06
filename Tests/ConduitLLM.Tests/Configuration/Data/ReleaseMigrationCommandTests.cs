using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Migrator;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Npgsql;

using IMigrator = Microsoft.EntityFrameworkCore.Migrations.IMigrator;

namespace ConduitLLM.Tests.Configuration.Data;

/// <summary>
/// End-to-end validation of the release migration command against a real PostgreSQL
/// server. CI supplies DATABASE_URL; local runs skip when it is absent.
/// </summary>
[Collection("MigrationEnvironment")]
public sealed class ReleaseMigrationCommandTests
{
    [SkippableFact]
    public async Task EmptyDatabase_BootstrapsEfAndWolverineSchemas()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var environment = database.UseAsMigrationTarget();

        var exitCode = await MigrationRunner.RunAsync();

        Assert.Equal(0, exitCode);
        await AssertSchemaCurrentAsync(database.ConnectionString);
        Assert.True(await SchemaExistsAsync(database.ConnectionString, "wolverine_conduit_gateway"));
        Assert.True(await SchemaExistsAsync(database.ConnectionString, "wolverine_conduit_admin"));
        Assert.True(await SchemaExistsAsync(database.ConnectionString, "wolverine_queues"));
    }

    [SkippableFact]
    public async Task PreviousSchema_UpgradesToCurrent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var environment = database.UseAsMigrationTarget();
        await using var context = CreateContext(database.ConnectionString);
        var migrations = context.Database.GetMigrations().ToArray();
        Skip.If(migrations.Length < 2, "At least two migrations are required to test an upgrade.");
        await context.GetService<IMigrator>().MigrateAsync(migrations[^2]);

        var exitCode = await MigrationRunner.RunAsync();

        Assert.Equal(0, exitCode);
        await AssertSchemaCurrentAsync(database.ConnectionString);
    }

    [SkippableFact]
    public async Task CompletedMigration_RetryIsSafeNoOp()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var environment = database.UseAsMigrationTarget(useInMemoryTransport: true);

        Assert.Equal(0, await MigrationRunner.RunAsync());
        var appliedBeforeRetry = await GetAppliedMigrationCountAsync(database.ConnectionString);

        Assert.Equal(0, await MigrationRunner.RunAsync());
        Assert.Equal(
            appliedBeforeRetry,
            await GetAppliedMigrationCountAsync(database.ConnectionString));
    }

    [SkippableFact]
    public async Task ConcurrentInvocations_AreSerializedAndSucceed()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var environment = database.UseAsMigrationTarget();

        var results = await Task.WhenAll(
            MigrationRunner.RunAsync(),
            MigrationRunner.RunAsync());

        Assert.All(results, exitCode => Assert.Equal(0, exitCode));
        await AssertSchemaCurrentAsync(database.ConnectionString);
    }

    [Fact]
    public async Task UnreachableDatabase_ReturnsFailureExitCode()
    {
        using var environment = new EnvironmentScope(
            ("DATABASE_URL",
                "Host=127.0.0.1;Port=1;Database=conduit_unreachable;" +
                "Username=conduit;Password=conduit;Timeout=1;Command Timeout=1"),
            ("ConduitLLM__Messaging__Wolverine__Transport", "InMemory"));

        var exitCode = await MigrationRunner.RunAsync();

        Assert.Equal(1, exitCode);
    }

    private static ConduitDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ConduitDbContext(options);
    }

    private static async Task AssertSchemaCurrentAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        var probe = new SchemaVersionProbe(new TestContextFactory(connectionString));
        var status = await probe.GetStatusAsync();
        Assert.True(status.IsCurrent);
        Assert.Equal(ConduitSchemaVersion.Current, status.AppliedVersion);
    }

    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => ReleaseMigrationCommandTests.CreateContext(connectionString);
    }

    private static async Task<int> GetAppliedMigrationCountAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        return (await context.Database.GetAppliedMigrationsAsync()).Count();
    }

    private static async Task<bool> SchemaExistsAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = @schema)",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private TemporaryDatabase(
            string adminConnectionString,
            string databaseName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable("DATABASE_URL");
            Skip.If(
                string.IsNullOrWhiteSpace(configured),
                "DATABASE_URL is required for release migration integration tests.");

            var source = ConfigurationDbContextFactory.ResolveNpgsqlConnectionString();
            var databaseName = $"conduit_release_migration_{Guid.NewGuid():N}";
            var adminBuilder = new NpgsqlConnectionStringBuilder(source)
            {
                Database = "postgres",
                Pooling = false
            };
            var targetBuilder = new NpgsqlConnectionStringBuilder(source)
            {
                Database = databaseName,
                Pooling = false
            };

            await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
            try
            {
                await connection.OpenAsync();
            }
            catch (Exception ex)
            {
                Skip.If(true, $"DATABASE_URL is not reachable: {ex.Message}");
            }

            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\"",
                connection);
            await command.ExecuteNonQueryAsync();

            return new TemporaryDatabase(
                adminBuilder.ConnectionString,
                databaseName,
                targetBuilder.ConnectionString);
        }

        public EnvironmentScope UseAsMigrationTarget(bool useInMemoryTransport = false)
            => new(
                ("DATABASE_URL", ConnectionString),
                ("CONDUIT_MIGRATION_MODE", "Wait"),
                ("ConduitLLM__Messaging__Wolverine__Transport",
                    useInMemoryTransport ? "InMemory" : "Postgresql"),
                ("ConduitLLM__Messaging__Wolverine__AutoProvision", "false"));

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using (var terminate = new NpgsqlCommand(
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @database AND pid <> pg_backend_pid()
                """,
                connection))
            {
                terminate.Parameters.AddWithValue("database", _databaseName);
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\"",
                connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly IReadOnlyList<(string Name, string? Value)> _saved;

        public EnvironmentScope(params (string Name, string? Value)[] variables)
        {
            _saved = variables
                .Select(variable =>
                    (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))
                .ToArray();
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
