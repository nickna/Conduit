using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.ModelCatalogs;
using ConduitLLM.Migrator;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using IMigrator = Microsoft.EntityFrameworkCore.Migrations.IMigrator;

namespace ConduitLLM.Tests.Configuration.Data;

/// <summary>
/// End-to-end validation of the release migration command against a real PostgreSQL
/// server. CI supplies DATABASE_URL; local runs skip when it is absent.
/// </summary>
[Collection("MigrationEnvironment")]
[Trait("Component", "ReleaseMigration")]
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
    public async Task FailingEfMigration_ReturnsFailureWithoutProvisioningAndCanRetryAfterRepair()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var environment = database.UseAsMigrationTarget();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        // The initial migration creates this table first. A pre-existing table in
        // this disposable database forces a real PostgreSQL DDL error, rather
        // than a connection failure before EF attempts any migration.
        await using (var conflict = new NpgsqlCommand(
            """
            CREATE TABLE "CacheConfigurationAudits" ("Conflict" integer)
            """, connection))
        {
            await conflict.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await MigrationRunner.RunAsync());
        Assert.Equal(0, await GetAppliedMigrationCountAsync(database.ConnectionString));
        var probe = new SchemaVersionProbe(new TestContextFactory(database.ConnectionString));
        Assert.False((await probe.GetStatusAsync()).IsCurrent);
        foreach (var schema in new[] { "wolverine_conduit_gateway", "wolverine_conduit_admin", "wolverine_queues" })
        {
            Assert.False(await SchemaExistsAsync(database.ConnectionString, schema));
        }

        await using (var repair = new NpgsqlCommand(
            """
            DROP TABLE "CacheConfigurationAudits"
            """, connection))
        {
            await repair.ExecuteNonQueryAsync();
        }

        Assert.Equal(0, await MigrationRunner.RunAsync());
        await AssertSchemaCurrentAsync(database.ConnectionString);
        foreach (var schema in new[] { "wolverine_conduit_gateway", "wolverine_conduit_admin", "wolverine_queues" })
        {
            Assert.True(await SchemaExistsAsync(database.ConnectionString, schema));
        }
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

    [SkippableFact]
    public async Task BundledCatalog_ProductionRetryStrategy_ImportsAndRepeatsIdempotently()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        await context.Database.MigrateAsync();
        var importer = new BundledModelCatalogImporter(
            new RetryingCatalogContextFactory(database.ConnectionString),
            new BundledModelCatalog(), NullLogger<BundledModelCatalogImporter>.Instance);

        var imported = await importer.ImportAsync(onlyWhenIdentifierCatalogIsEmpty: true);
        Assert.NotNull(imported);
        Assert.True(imported.Created.Identifiers > 0);
        Assert.Null(await importer.ImportAsync(onlyWhenIdentifierCatalogIsEmpty: true));
        var repeated = await importer.ImportAsync(onlyWhenIdentifierCatalogIsEmpty: false);
        Assert.NotNull(repeated);
        Assert.Equal(0, repeated.Created.Identifiers);
        Assert.Equal(imported.Created.Identifiers, repeated.SkippedExistingIdentifiers);
        Assert.Equal(imported.Created.Identifiers, await context.ModelProviderTypeAssociations.CountAsync());
    }

    [SkippableFact]
    public async Task BundledCatalog_FailureAfterSave_RetriesWholeTransactionWithFreshContext()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var context = CreateContext(database.ConnectionString);
        await context.Database.MigrateAsync();
        var failure = new FailFirstCatalogSave();
        var factory = new RetryingCatalogContextFactory(database.ConnectionString, failure);
        var importer = new BundledModelCatalogImporter(factory,
            new BundledModelCatalog(), NullLogger<BundledModelCatalogImporter>.Instance);

        var imported = await importer.ImportAsync(onlyWhenIdentifierCatalogIsEmpty: true);

        Assert.NotNull(imported);
        Assert.Equal(2, failure.Saves);
        Assert.Equal(3, factory.ContextsCreated); // Strategy plus one context per attempt.
        Assert.Equal(imported.Created.Identifiers, await context.ModelProviderTypeAssociations.CountAsync());
        Assert.Equal(imported.Created.Models, await context.Models.CountAsync());
    }

    private sealed class RetryingCatalogContextFactory(string connectionString, SaveChangesInterceptor? interceptor = null)
        : IDbContextFactory<ConduitDbContext>
    {
        public int ContextsCreated { get; private set; }

        public ConduitDbContext CreateDbContext()
        {
            ContextsCreated++;
            var options = new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure(
                    maxRetryCount: 2, maxRetryDelay: TimeSpan.Zero, errorCodesToAdd: null));
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new ConduitDbContext(options.Options);
        }
    }

    private sealed class FailFirstCatalogSave : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (++Saves == 1)
                throw new PostgresException("Injected serialization failure after save", "ERROR", "ERROR", "40001");
            return ValueTask.FromResult(result);
        }
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
            if (string.IsNullOrWhiteSpace(configured) &&
                (Environment.GetEnvironmentVariable("CI") == "true" ||
                 Environment.GetEnvironmentVariable("CONDUIT_CI_REQUIRED_INFRASTRUCTURE") == "true"))
            {
                throw new InvalidOperationException("Required CI migration database DATABASE_URL is missing.");
            }
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
            // A configured database is a promise, including in local runs. Never
            // turn an infrastructure outage into a successful skipped CI gate.
            await connection.OpenAsync();

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
