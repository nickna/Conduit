using ConduitLLM.Configuration.Data;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConduitLLM.Configuration.HealthChecks
{
    /// <summary>
    /// Checks the live schema version on every Wait-mode readiness request, including
    /// after startup has completed. Must be registered with the "ready" tag.
    /// Explicit Skip mode bypasses the probe.
    /// </summary>
    public class PendingMigrationsReadinessCheck : IHealthCheck
    {
        private readonly ISchemaVersionProbe _probe;
        private readonly MigrationStartupOptions _options;

        public PendingMigrationsReadinessCheck(
            ISchemaVersionProbe probe, MigrationStartupOptions options)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            if (_options.Mode == MigrationMode.Skip)
            {
                return HealthCheckResult.Healthy("Database schema checks are disabled in Skip mode");
            }

            try
            {
                var status = await _probe.GetStatusAsync(cancellationToken);
                return status.IsCurrent
                    ? HealthCheckResult.Healthy("Database schema is current")
                    : HealthCheckResult.Unhealthy(
                        $"Database schema is at {status.AppliedVersion ?? "<uninitialized>"}; " +
                        $"this release requires {status.ExpectedVersion}. " +
                        "Run the ConduitLLM.Migrator deployment job to migrate before rollout.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy(
                    "Could not check the database schema version", exception);
            }
        }
    }
}
