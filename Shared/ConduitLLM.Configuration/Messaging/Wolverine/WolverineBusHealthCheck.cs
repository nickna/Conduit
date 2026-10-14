using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Wolverine.Persistence.Durability;

namespace ConduitLLM.Configuration.Messaging.Wolverine
{
    /// <summary>
    /// Health check for the Wolverine messaging backend (I2.8/#931). Probes the Postgres
    /// message store (inbox/outbox/scheduled/dead-letter counts), so it verifies the
    /// durability layer end-to-end rather than just DI resolution.
    /// </summary>
    /// <remarks>
    /// Only meaningful on the Postgresql transport — hosts gate registration on
    /// <see cref="WolverineMessagingExtensions.UsesInMemoryTransport"/> being false
    /// (the in-memory dev/CI mode has no message store to probe).
    /// <para>
    /// Status mapping: store unreachable → Unhealthy (the bus cannot persist or deliver);
    /// dead-letter count at or above the threshold → Degraded (messages are exhausting
    /// their retries — for the financial queues that warrants investigation); otherwise
    /// Healthy. The queue counts are always reported in the health entry's data.
    /// </para>
    /// </remarks>
    public class WolverineBusHealthCheck : IHealthCheck
    {
        private readonly IMessageStore _messageStore;
        private readonly ILogger<WolverineBusHealthCheck> _logger;
        private readonly int _deadLetterDegradedThreshold;
        private readonly WolverineRuntimeStartupState? _startup;

        /// <summary>
        /// Configuration key for the dead-letter Degraded threshold (default 1 — any
        /// dead-lettered message marks the bus Degraded).
        /// </summary>
        public const string DeadLetterThresholdKey =
            "ConduitLLM:Messaging:Wolverine:HealthCheck:DeadLetterDegradedThreshold";

        /// <summary>
        /// Initializes the health check.
        /// </summary>
        /// <param name="messageStore">Wolverine's message store (registered by the Postgresql transport).</param>
        /// <param name="logger">Logger.</param>
        /// <param name="deadLetterDegradedThreshold">Dead-letter count at which the bus reports Degraded.</param>
        public WolverineBusHealthCheck(
            IMessageStore messageStore,
            ILogger<WolverineBusHealthCheck> logger,
            int deadLetterDegradedThreshold)
            : this(messageStore, logger, deadLetterDegradedThreshold, null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public WolverineBusHealthCheck(
            IMessageStore messageStore,
            ILogger<WolverineBusHealthCheck> logger,
            int deadLetterDegradedThreshold = 1,
            WolverineRuntimeStartupState? startup = null)
        {
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deadLetterDegradedThreshold = deadLetterDegradedThreshold;
            _startup = startup;
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            if (_startup is { IsReady: false })
                return HealthCheckResult.Unhealthy("Wolverine runtime has not completed schema-gated startup");
            try
            {
                var counts = await _messageStore.Admin.FetchCountsAsync();

                var data = new Dictionary<string, object>
                {
                    ["incoming"] = counts.Incoming,
                    ["outgoing"] = counts.Outgoing,
                    ["scheduled"] = counts.Scheduled,
                    ["dead_letter"] = counts.DeadLetter,
                    ["handled"] = counts.Handled
                };

                if (counts.DeadLetter >= _deadLetterDegradedThreshold)
                {
                    return HealthCheckResult.Degraded(
                        $"Wolverine bus reachable but {counts.DeadLetter} message(s) are dead-lettered " +
                        $"(threshold {_deadLetterDegradedThreshold})",
                        data: data);
                }

                return HealthCheckResult.Healthy("Wolverine bus and message store reachable", data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wolverine message store health probe failed");
                return HealthCheckResult.Unhealthy(
                    "Wolverine message store unreachable - the bus cannot persist or deliver messages",
                    ex);
            }
        }
    }
}
