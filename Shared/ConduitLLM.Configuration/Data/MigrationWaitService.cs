using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Data
{
    /// <summary>
    /// In Wait mode, polls until the database schema version matches this binary
    /// (the standalone migrator applies it), then flips
    /// <see cref="MigrationReadinessState"/> to record initial wait completion.
    /// Live readiness continues probing independently. No-op in Skip mode.
    /// An unreachable database is not fatal: readiness simply stays
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
                        _logger.LogInformation("Database schema is current. Initial schema wait completed.");
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
