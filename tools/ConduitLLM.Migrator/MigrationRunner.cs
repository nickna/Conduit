using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Configuration.ModelCatalogs;

using JasperFx.Resources;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Wolverine;
using Wolverine.Postgresql;

namespace ConduitLLM.Migrator;

/// <summary>The release-owned schema mutation entry point.</summary>
public static class MigrationRunner
{
    private const long ReleaseJobLockId = 7_891_099;

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("Conduit.Migrator");

        try
        {
            var options = MigrationStartupOptions.ForMigrator(logger);
            var connectionString = ConfigurationDbContextFactory.ResolveNpgsqlConnectionString();
            await using var releaseLock = new NpgsqlConnection(connectionString);
            await releaseLock.OpenAsync(cancellationToken);
            await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({ReleaseJobLockId})", releaseLock))
            {
                acquire.CommandTimeout = options.LockTimeoutSeconds;
                await acquire.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                var contextOptions = new DbContextOptionsBuilder<ConduitDbContext>()
                    .UseNpgsql(connectionString)
                    .Options;
                var contextFactory = new FixedOptionsDbContextFactory(contextOptions);
                var catalogImporter = new BundledModelCatalogImporter(
                    contextFactory,
                    new BundledModelCatalog(),
                    loggerFactory.CreateLogger<BundledModelCatalogImporter>());
                var migrationService = new SimpleMigrationService(
                    contextFactory,
                    options,
                    loggerFactory.CreateLogger<SimpleMigrationService>(),
                    catalogImporter);

                await migrationService.MigrateAsync(cancellationToken);
                await ProvisionWolverineStorageAsync(connectionString, logger, cancellationToken);
            }
            finally
            {
                await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({ReleaseJobLockId})", releaseLock);
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
            logger.LogInformation("Migration completed successfully");
            return 0;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Migration failed");
            return 1;
        }
    }

    private static async Task ProvisionWolverineStorageAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (MessagingBackendResolver.Resolve(configuration) != MessagingBackend.Wolverine)
        {
            logger.LogInformation("Messaging backend is not Wolverine; skipping bus schema provisioning");
            return;
        }

        if (WolverineMessagingExtensions.UsesInMemoryTransport(configuration))
        {
            logger.LogInformation("Wolverine transport is InMemory; no bus schema to provision");
            return;
        }

        await ProvisionWolverineHostAsync(
            configuration, connectionString, "conduit-gateway", WolverineQueueNames.Gateway, cancellationToken);
        await ProvisionWolverineHostAsync(
            configuration, connectionString, "conduit-admin", WolverineQueueNames.Admin, cancellationToken);
    }

    private static async Task ProvisionWolverineHostAsync(
        IConfiguration configuration,
        string connectionString,
        string serviceName,
        IReadOnlyList<string> queueNames,
        CancellationToken cancellationToken)
    {
        using var host = Host.CreateDefaultBuilder()
            .AddConduitWolverine(configuration, connectionString, serviceName, options =>
            {
                foreach (var queueName in queueNames)
                {
                    options.ListenToPostgresqlQueue(queueName);
                }
            })
            .Build();
        await host.SetupResources(cancellationToken);
    }

    private sealed class FixedOptionsDbContextFactory(DbContextOptions<ConduitDbContext> options)
        : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(options);
    }
}
