using ConduitLLM.Configuration.Data;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConduitLLM.Configuration.HealthChecks
{
    /// <summary>
    /// Fails readiness until <see cref="MigrationReadinessState"/> reports the schema
    /// current. Must be registered with the "ready" tag — /health/ready filters on it.
    /// In Skip mode the state starts current; otherwise it holds readiness at 503 until
    /// the external migrator has applied all migrations this binary knows about.
    /// </summary>
    public class PendingMigrationsReadinessCheck : IHealthCheck
    {
        private readonly MigrationReadinessState _state;

        public PendingMigrationsReadinessCheck(MigrationReadinessState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_state.IsSchemaCurrent
                ? HealthCheckResult.Healthy("Database schema is current")
                : HealthCheckResult.Unhealthy(
                    "Database schema is not current. Run the ConduitLLM.Migrator deployment job to migrate before rollout."));
        }
    }
}
