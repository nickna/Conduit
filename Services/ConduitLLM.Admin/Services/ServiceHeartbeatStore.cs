using System.Collections.Concurrent;
using System.Text.Json;

using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Core.Constants;

using StackExchange.Redis;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Redis-backed <see cref="IServiceHeartbeatStore"/> (#1067). The last heartbeat is written
    /// under a short TTL so a present value always means "seen recently"; the reader still
    /// derives health from the recorded age, not mere key presence. When Redis is unavailable
    /// (single-instance/dev) it degrades to an in-process snapshot so the dashboard keeps
    /// working — mirrors the optional-<c>IConnectionMultiplexer</c> pattern used by
    /// <see cref="MediaCleanupStatusService"/>.
    /// </summary>
    public class ServiceHeartbeatStore : IServiceHeartbeatStore
    {
        /// <summary>
        /// TTL for the persisted snapshot. Comfortably longer than several heartbeat intervals
        /// so a brief gap doesn't evict it; the reader decides staleness from the age.
        /// </summary>
        private static readonly TimeSpan MinimumSnapshotTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaximumSnapshotTtl = TimeSpan.FromMinutes(30);

        private readonly IConnectionMultiplexer? _redis;
        private readonly ILogger<ServiceHeartbeatStore> _logger;
        private readonly ConcurrentDictionary<string, ServiceHeartbeatSnapshot> _inProcess = new();
        private readonly ConcurrentDictionary<string, ServiceHeartbeatSnapshot> _latestByService = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceHeartbeatStore"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="redis">The Redis connection, or <c>null</c> when Redis is not configured.</param>
        public ServiceHeartbeatStore(
            ILogger<ServiceHeartbeatStore> logger,
            IConnectionMultiplexer? redis = null)
        {
            _logger = logger;
            _redis = redis;
        }

        /// <inheritdoc />
        public async Task RecordAsync(ServiceHeartbeatSnapshot heartbeat, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(heartbeat);

            // Always keep an in-process copy so a Redis-less single instance still works.
            var instanceKey = GetInProcessKey(heartbeat.ServiceId, heartbeat.Heartbeat.InstanceId);
            _inProcess[instanceKey] = heartbeat;
            _latestByService[heartbeat.ServiceId] = heartbeat;

            if (_redis == null)
            {
                return;
            }

            try
            {
                var db = _redis.GetDatabase();
                var json = AdminJson.Serialize(heartbeat);
                var ttl = CalculateTtl(heartbeat.Heartbeat.IntervalSeconds);
                await db.StringSetAsync(
                    RedisKeys.ServiceHeartbeat.For(heartbeat.ServiceId, heartbeat.Heartbeat.InstanceId),
                    json,
                    ttl);
                await db.SetAddAsync(
                    RedisKeys.ServiceHeartbeat.Index(heartbeat.ServiceId),
                    heartbeat.Heartbeat.InstanceId);
                await db.KeyExpireAsync(RedisKeys.ServiceHeartbeat.Index(heartbeat.ServiceId), ttl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist {ServiceId} heartbeat to Redis; using in-process fallback",
                    heartbeat.ServiceId);
            }
        }

        /// <inheritdoc />
        public async Task<ServiceHeartbeatSnapshot?> GetAsync(string serviceId, CancellationToken cancellationToken = default)
        {
            var snapshots = await GetAllAsync(serviceId, cancellationToken);
            if (snapshots.Count > 0)
            {
                return snapshots
                    .OrderByDescending(snapshot => snapshot.ReceivedAtUtc)
                    .First();
            }

            return _latestByService.TryGetValue(serviceId, out var latest)
                && !IsExpired(latest, DateTime.UtcNow)
                    ? latest
                    : null;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ServiceHeartbeatSnapshot>> GetAllAsync(
            string serviceId,
            CancellationToken cancellationToken = default)
        {
            if (_redis != null)
            {
                try
                {
                    var db = _redis.GetDatabase();
                    var indexKey = RedisKeys.ServiceHeartbeat.Index(serviceId);
                    var instanceIds = await db.SetMembersAsync(indexKey);
                    if (instanceIds.Length > 0)
                    {
                        var keys = instanceIds
                            .Select(value => (RedisKey)RedisKeys.ServiceHeartbeat.For(
                                serviceId,
                                value.ToString()))
                            .ToArray();
                        var values = await db.StringGetAsync(keys);
                        var snapshots = new List<ServiceHeartbeatSnapshot>(values.Length);
                        var expiredMembers = new List<RedisValue>();

                        for (var index = 0; index < values.Length; index++)
                        {
                            if (values[index].IsNullOrEmpty)
                            {
                                expiredMembers.Add(instanceIds[index]);
                                continue;
                            }

                            var snapshot = DeserializeSnapshot(values[index].ToString());
                            if (snapshot != null)
                            {
                                snapshots.Add(snapshot);
                            }
                        }

                        if (expiredMembers.Count > 0)
                        {
                            await db.SetRemoveAsync(indexKey, expiredMembers.ToArray());
                        }

                        if (snapshots.Count > 0)
                        {
                            return snapshots;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to read {ServiceId} heartbeats from Redis; falling back to in-process copies",
                        serviceId);
                }
            }

            var now = DateTime.UtcNow;
            var snapshotsInProcess = new List<ServiceHeartbeatSnapshot>();
            foreach (var pair in _inProcess)
            {
                var snapshot = pair.Value;
                if (!snapshot.ServiceId.Equals(serviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsExpired(snapshot, now))
                {
                    _inProcess.TryRemove(pair.Key, out _);
                    continue;
                }

                snapshotsInProcess.Add(snapshot);
            }

            return snapshotsInProcess;
        }

        internal static TimeSpan CalculateTtl(double intervalSeconds)
        {
            var cadence = intervalSeconds > 0
                ? TimeSpan.FromSeconds(intervalSeconds)
                : TimeSpan.FromSeconds(ServiceHeartbeatEvaluator.DefaultIntervalSeconds);
            var calculated = TimeSpan.FromTicks(cadence.Ticks * 10);
            return calculated < MinimumSnapshotTtl
                ? MinimumSnapshotTtl
                : calculated > MaximumSnapshotTtl
                    ? MaximumSnapshotTtl
                    : calculated;
        }

        internal static ServiceHeartbeatSnapshot? DeserializeSnapshot(string json)
        {
            using var document = JsonDocument.Parse(json);
            var hasEmbeddedHeartbeat = document.RootElement.EnumerateObject()
                .Any(property => property.Name.Equals(
                    nameof(ServiceHeartbeatSnapshot.Heartbeat),
                    StringComparison.OrdinalIgnoreCase));

            if (hasEmbeddedHeartbeat)
            {
                return AdminJson.Deserialize<ServiceHeartbeatSnapshot>(json);
            }

            var legacy = AdminJson.Deserialize<LegacyServiceHeartbeatSnapshot>(json);
            if (legacy is null)
            {
                return null;
            }

            return new ServiceHeartbeatSnapshot
            {
                ServiceId = legacy.ServiceId,
                Heartbeat = new ConduitLLM.Core.Events.GatewayHeartbeat
                {
                    InstanceId = legacy.InstanceId,
                    Version = legacy.Version,
                    CommitSha = legacy.CommitSha,
                    BuildTimestamp = legacy.BuildTimestamp,
                    Status = legacy.Status,
                    UptimeSeconds = legacy.UptimeSeconds,
                    IntervalSeconds = legacy.IntervalSeconds,
                    Timestamp = legacy.ReportedAtUtc
                },
                ReportedAtUtc = legacy.ReportedAtUtc,
                ReceivedAtUtc = legacy.ReceivedAtUtc
            };
        }

        private static bool IsExpired(ServiceHeartbeatSnapshot snapshot, DateTime now) =>
            snapshot.ReceivedAtUtc != default
            && now - snapshot.ReceivedAtUtc > CalculateTtl(snapshot.Heartbeat.IntervalSeconds);

        private static string GetInProcessKey(string serviceId, string instanceId) =>
            $"{serviceId}\n{instanceId}";

        internal sealed class LegacyServiceHeartbeatSnapshot
        {
            public string ServiceId { get; set; } = string.Empty;
            public string InstanceId { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string CommitSha { get; set; } = "dev";
            public string BuildTimestamp { get; set; } = "unknown";
            public string Status { get; set; } = "healthy";
            public double UptimeSeconds { get; set; }
            public double IntervalSeconds { get; set; }
            public DateTime ReportedAtUtc { get; set; }
            public DateTime ReceivedAtUtc { get; set; }
        }
    }
}
