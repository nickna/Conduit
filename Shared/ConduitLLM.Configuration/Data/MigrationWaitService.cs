using System.Diagnostics;

using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Data
{
    /// <summary>
    /// Schema version compiled into this release. Update this value whenever a migration is added.
    /// </summary>
    public static class ConduitSchemaVersion
    {
        public const string Current = "20260805172716_AddAsyncTaskRetryDispatchId";
    }

    public sealed record SchemaVersionStatus(string? AppliedVersion, string ExpectedVersion)
    {
        public bool IsCurrent => string.Equals(
            AppliedVersion,
            ExpectedVersion,
            StringComparison.Ordinal);
    }

    /// <summary>Reads only the latest row from EF's stable migration-history table.</summary>
    public interface ISchemaVersionProbe
    {
        Task<SchemaVersionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Probes the schema version through the pooled context factory without loading EF migration metadata.
    /// </summary>
    public sealed class SchemaVersionProbe : ISchemaVersionProbe
    {
        private readonly IDbContextFactory<ConduitDbContext> _contextFactory;

        public SchemaVersionProbe(IDbContextFactory<ConduitDbContext> contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task<SchemaVersionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT CASE
                    WHEN to_regclass('"__EFMigrationsHistory"') IS NULL THEN NULL
                    ELSE (SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1)
                END
                """;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return new SchemaVersionStatus(
                value is null or DBNull ? null : Convert.ToString(value),
                ConduitSchemaVersion.Current);
        }
    }

    /// <summary>
    /// In Wait mode, polls until the database schema version matches this binary
    /// (the standalone migrator applies it), then flips
    /// <see cref="MigrationReadinessState"/> so /health/ready starts passing. No-op in
    /// Skip mode. An unreachable database is not fatal: readiness simply stays
    /// down with an actionable diagnostic, which is the correct signal for orchestrators.
    /// </summary>
    public sealed class MigrationWaitService : BackgroundService
    {
        private static readonly TimeSpan InitialPollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(15);

        private readonly MigrationStartupOptions _options;
        private readonly ISchemaVersionProbe _probe;
        private readonly MigrationReadinessState _state;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<MigrationWaitService> _logger;

        public MigrationWaitService(
            MigrationStartupOptions options,
            ISchemaVersionProbe probe,
            MigrationReadinessState state,
            IHostApplicationLifetime lifetime,
            ILogger<MigrationWaitService> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.Mode != MigrationMode.Wait)
            {
                return;
            }

            // Yield so host startup (and Kestrel binding) proceeds while we poll —
            // /health/ready must be reachable and failing during the wait.
            await Task.Yield();

            _logger.LogInformation(
                "This service never applies database migrations. Polling until the schema is current; " +
                "/health/ready is gated until then. Run the ConduitLLM.Migrator deployment job before rollout.");

            var elapsed = Stopwatch.StartNew();
            var interval = InitialPollInterval;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var status = await _probe.GetStatusAsync(stoppingToken);
                    if (status.IsCurrent)
                    {
                        _state.IsSchemaCurrent = true;
                        _logger.LogInformation("Database schema is current. Service is ready.");
                        return;
                    }

                    _logger.LogInformation(
                        "Database schema is at {AppliedVersion}; this release requires {ExpectedVersion}. " +
                        "Run the ConduitLLM.Migrator deployment job before rollout.",
                        status.AppliedVersion ?? "<uninitialized>", status.ExpectedVersion);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not check the database schema version; will retry. Readiness remains down.");
                }

                if (_options.WaitTimeoutSeconds > 0 && elapsed.Elapsed.TotalSeconds >= _options.WaitTimeoutSeconds)
                {
                    _logger.LogCritical(
                        "Schema did not become current within {WaitTimeoutVariable}={TimeoutSeconds}s. " +
                        "Run the ConduitLLM.Migrator deployment job, then restart the service. Stopping application.",
                        MigrationStartupOptions.WaitTimeoutVariable, _options.WaitTimeoutSeconds);
                    _lifetime.StopApplication();
                    return;
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (interval < MaxPollInterval)
                {
                    interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds + 2.5, MaxPollInterval.TotalSeconds));
                }
            }
        }
    }
}
