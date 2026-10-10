namespace ConduitLLM.Core.Constants;

/// <summary>
/// Centralized Redis key patterns for all operational (non-cache) Redis usage.
/// Covers rate limiting, webhooks, SignalR infrastructure, distributed locks,
/// cache statistics, and spend notifications.
///
/// For cache-layer keys (virtual keys, model costs, providers, etc.),
/// see <see cref="ConduitLLM.Configuration.Constants.CacheKeys"/>.
/// </summary>
/// <remarks>
/// Key naming conventions:
/// - Colons as namespace separators (e.g., "rate:vk:{hash}:rpm")
/// - Lowercase for static parts
/// - Builder methods handle dynamic key construction
/// - Each nested class owns one key namespace to prevent collisions
/// </remarks>
public static class RedisKeys
{
    #region Async Tasks

    /// <summary>
    /// Keys for async task state caching. Written by HybridAsyncTaskService and
    /// invalidated by AsyncTaskCacheInvalidationHandler — both must share this shape.
    /// </summary>
    public static class AsyncTask
    {
        public const string Prefix = "async:task:";

        public static string For(string taskId) => $"{Prefix}{taskId}";
    }

    #endregion

    #region Rate Limiting

    /// <summary>
    /// Keys for virtual key rate limiting (sliding window sorted sets).
    /// Used by RedisVirtualKeyRateLimitService.
    /// </summary>
    public static class RateLimit
    {
        public static string VirtualKeyRpm(string hash) => $"rate:vk:{hash}:rpm";
        public static string VirtualKeyRpd(string hash) => $"rate:vk:{hash}:rpd";

        /// <summary>Weighted token-per-minute window; entry weights are token counts.</summary>
        public static string VirtualKeyTpm(string hash) => $"rate:vk:{hash}:tpm";

        /// <summary>
        /// In-flight request slots. Entries are released explicitly when a request finishes and
        /// age out on their own if the node holding them dies.
        /// </summary>
        public static string VirtualKeyConcurrency(string hash) => $"rate:vk:{hash}:concurrency";

        // Per-model overrides are partitioned by the alias the caller sends, which is what the
        // operator configured a ceiling against.
        public static string VirtualKeyModelRpm(string hash, string modelAlias) =>
            $"rate:vk:{hash}:model:{modelAlias}:rpm";

        public static string VirtualKeyModelTpm(string hash, string modelAlias) =>
            $"rate:vk:{hash}:model:{modelAlias}:tpm";

        // Group-scope windows. Every key in a group shares these, so the partition is the
        // group id rather than a key hash.
        public static string GroupRpm(int groupId) => $"rate:vkg:{groupId}:rpm";
        public static string GroupRpd(int groupId) => $"rate:vkg:{groupId}:rpd";
        public static string GroupTpm(int groupId) => $"rate:vkg:{groupId}:tpm";
        public static string GroupConcurrency(int groupId) => $"rate:vkg:{groupId}:concurrency";

        /// <summary>
        /// Companion key holding the total weight currently inside a sliding window, so reading
        /// the window's usage is O(1). Written by the limiter's Lua script — this builder exists
        /// for cleanup paths that need to delete a window wholesale.
        /// </summary>
        public static string WindowSum(string windowKey) => $"{windowKey}:sum";
    }

    /// <summary>
    /// Keys for SignalR rate limiting and connection tracking.
    /// Used by RedisSignalRRateLimitService.
    /// </summary>
    public static class SignalRRateLimit
    {
        public static string Rpm(string hash) => $"signalr:vk:{hash}:rpm";
        public static string Rpd(string hash) => $"signalr:vk:{hash}:rpd";
        public static string Connections(string hash) => $"signalr:vk:{hash}:connections";
    }

    #endregion

    #region Webhooks

    /// <summary>
    /// Keys for webhook circuit breaker state.
    /// Legacy expiring webhook circuit keys; new admission uses leased hashed keys.
    /// </summary>
    public static class WebhookCircuit
    {
        public static string State(string urlHash) => $"webhook:circuit:{urlHash}:state";
        public static string Failures(string urlHash) => $"webhook:circuit:{urlHash}:failures";
        public static string Successes(string urlHash) => $"webhook:circuit:{urlHash}:success";
        public static string LastFailure(string urlHash) => $"webhook:circuit:{urlHash}:lastfail";
        public static string Opened(string urlHash) => $"webhook:circuit:{urlHash}:opened";
    }

    /// <summary>
    /// Keys for webhook delivery deduplication and statistics.
    /// Used by RedisWebhookDeliveryTracker.
    /// </summary>
    public static class WebhookDelivery
    {
        public static string Delivered(string deliveryKey) => $"webhook:delivered:{deliveryKey}";
        public static string Stats(string webhookUrl) => $"webhook:stats:{webhookUrl}";
        public static string Failure(string deliveryKey) => $"webhook:failure:{deliveryKey}";
    }

    /// <summary>
    /// Keys for webhook metrics aggregation.
    /// Used by RedisWebhookMetricsService.
    /// </summary>
    public static class WebhookMetrics
    {
        /// <summary>Recent delivery events list.</summary>
        public const string RecentEvents = "webhook:events:recent";

        /// <summary>Pattern for scanning all URL metrics keys.</summary>
        public const string UrlMetricsScanPattern = "webhook:metrics:urls:*";

        public static string UrlMetrics(string urlHash) => $"webhook:metrics:urls:{urlHash}";
        public static string ResponseTimes(string urlHash) => $"webhook:metrics:response:{urlHash}";
    }

    /// <summary>
    /// Keys for webhook connection tracking (which connections monitor which webhooks).
    /// Used by RedisWebhookConnectionTracker.
    /// </summary>
    public static class WebhookConnection
    {
        public static string ConnectionWebhooks(string connectionId) => $"webhook:connections:{connectionId}:webhooks";
        public static string WebhookConnections(string urlHash) => $"webhook:webhooks:{urlHash}:connections";
        public static string ConnectionTimestamp(string connectionId) => $"webhook:connections:{connectionId}:timestamp";
    }

    #endregion

    #region Distributed Locks

    /// <summary>
    /// Keys for distributed locking.
    /// Stable coordination identities, including the PostgreSQL alert guard.
    /// </summary>
    public static class Lock
    {
        public static string AlertThreshold(string virtualKeyId, string threshold) => $"lock:alert:vk:{virtualKeyId}:threshold:{threshold}";
    }

    #endregion

    #region Spend Notifications

    /// <summary>
    /// Keys for spend notification and alerting.
    /// Used by SpendDataRepository.
    /// </summary>
    public static class Spend
    {
        public const string PatternsPrefix = "spend:patterns";
        public const string SentAlertsPrefix = "spend:alerts:sent";
        public const string CooldownPrefix = "spend:alerts:cooldown";
        public const string HistoryStream = "spend:history:stream";
        public const string NotificationInstances = "spend:notification:instances";

        public static string Patterns(string virtualKeyId) => $"spend:patterns:{virtualKeyId}";
        public static string PatternsScanPattern() => $"{PatternsPrefix}:*";
        public static string SentAlert(string virtualKeyId, string threshold) => $"spend:alerts:sent:{virtualKeyId}:{threshold}";
        public static string SentAlertScanPattern(string virtualKeyId) => $"spend:alerts:sent:{virtualKeyId}:*";
        public static string Cooldown(string virtualKeyId, string alertType) => $"spend:alerts:cooldown:{virtualKeyId}:{alertType}";
        public static string Instance(string instanceId) => $"spend:notification:instances:{instanceId}";
    }

    #endregion

    #region Service Health

    /// <summary>
    /// Keys for cross-service liveness heartbeats (#1067). The Gateway publishes a heartbeat
    /// event; the Admin records the last-seen snapshot under these keys so the health dashboard
    /// can report a service's real status from staleness. Used by ServiceHeartbeatStore.
    /// </summary>
    public static class ServiceHeartbeat
    {
        /// <summary>Logical service id for the Gateway ("core-api") heartbeat.</summary>
        public const string GatewayServiceId = "gateway";

        /// <summary>Logical service id for Admin API heartbeats.</summary>
        public const string AdminServiceId = "admin";

        /// <summary>Heartbeat snapshot for a specific service instance.</summary>
        public static string For(string serviceId, string instanceId) =>
            $"health:heartbeat:{serviceId}:{instanceId}";

        /// <summary>Index of instance ids that have reported for a logical service.</summary>
        public static string Index(string serviceId) => $"health:heartbeat:{serviceId}:instances";
    }

    #endregion
}
