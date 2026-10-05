namespace ConduitLLM.Configuration.Constants;

/// <summary>
/// Centralized cache key patterns for all services using Redis/distributed cache.
/// Use these constants to ensure consistent key naming and avoid collisions.
/// </summary>
/// <remarks>
/// Key naming conventions:
/// - Use colons as separators (e.g., "vkey:hash:abc123")
/// - Keep prefixes short but descriptive
/// - Use lowercase for static parts
/// - Builder methods handle dynamic key construction
/// </remarks>
public static class CacheKeys
{
    #region Virtual Key Cache

    /// <summary>
    /// Cache keys for Virtual Key authentication and validation.
    /// Used by RedisVirtualKeyCache for high-performance key lookups.
    /// </summary>
    public static class VirtualKey
    {
        /// <summary>Prefix for all virtual key cache entries</summary>
        public const string Prefix = "vkey:";

        /// <summary>Channel for single key invalidation notifications</summary>
        public const string InvalidationChannel = "vkey_invalidated";

        /// <summary>Channel for batch key invalidation notifications</summary>
        public const string BatchInvalidationChannel = "vkey_batch_invalidated";

        /// <summary>Builds a cache key for a virtual key by its hash</summary>
        /// <param name="keyHash">The hashed key value</param>
        /// <returns>Full cache key like "vkey:abc123"</returns>
        public static string ByHash(string keyHash) => $"{Prefix}{keyHash}";
    }

    #endregion

    #region Model Cost Cache

    /// <summary>
    /// Cache keys for Model Cost lookups and pattern matching.
    /// Used by RedisModelCostCache for cost calculations.
    /// </summary>
    public static class ModelCost
    {
        /// <summary>Prefix for model cost entries by ID</summary>
        public const string Prefix = "modelcost:";

        /// <summary>Key for all model costs list cache</summary>
        public const string All = "modelcost:all";

        /// <summary>Prefix for model cost pattern lookups</summary>
        public const string PatternPrefix = "modelcost:pattern:";

        /// <summary>Prefix for provider-based model cost groupings (deprecated)</summary>
        public const string ProviderPrefix = "modelcost:provider:";

        /// <summary>Channel for cost invalidation notifications</summary>
        public const string InvalidationChannel = "mcost_invalidated";

        /// <summary>Channel for batch cost invalidation notifications</summary>
        public const string BatchInvalidationChannel = "mcost_batch_invalidated";

        /// <summary>Builds a cache key for a model cost by pattern</summary>
        /// <param name="modelIdPattern">The model ID pattern (case-insensitive)</param>
        /// <returns>Full cache key like "modelcost:pattern:gpt-4"</returns>
        public static string ByPattern(string modelIdPattern) => $"{PatternPrefix}{modelIdPattern.ToLowerInvariant()}";

        /// <summary>Builds a cache key for a model cost by model ID</summary>
        /// <param name="modelId">The model ID</param>
        /// <returns>Full cache key like "modelcost:pattern:gpt-4-turbo"</returns>
        public static string ByModelId(string modelId) => ByPattern(modelId);

        /// <summary>Builds a cache key for a model cost by its database ID</summary>
        /// <param name="id">The model cost ID</param>
        /// <returns>Full cache key like "modelcost:id:123"</returns>
        public static string ById(int id) => $"modelcost:id:{id}";
    }

    #endregion

    #region Pricing Rules Cache

    /// <summary>
    /// Cache keys for parsed pricing rules configurations.
    /// Used by FusionPricingRulesService for deserialized PricingRulesConfig caching.
    /// </summary>
    public static class PricingRules
    {
        /// <summary>Prefix for pricing rules cache entries</summary>
        public const string Prefix = "pricingrules:";

        /// <summary>Builds a cache key for pricing rules by model cost ID</summary>
        /// <param name="modelCostId">The model cost ID</param>
        /// <returns>Full cache key like "pricingrules:id:123"</returns>
        public static string ById(int modelCostId) => $"pricingrules:id:{modelCostId}";
    }

    #endregion

    #region Global Setting Cache

    /// <summary>
    /// Cache keys for Global Settings.
    /// Used by RedisGlobalSettingCache for application configuration.
    /// </summary>
    public static class GlobalSetting
    {
        /// <summary>Prefix for all global setting cache entries</summary>
        public const string Prefix = "globalsetting:";

        /// <summary>Special key for authentication key caching with shorter TTL</summary>
        public const string AuthKey = "globalsetting:authkey";

        /// <summary>Builds a cache key for a global setting by key name</summary>
        /// <param name="settingKey">The setting key name</param>
        /// <returns>Full cache key like "globalsetting:maxrequests"</returns>
        public static string ByKey(string settingKey) => $"{Prefix}{settingKey.ToLowerInvariant()}";
    }

    #endregion

    #region Provider Cache

    /// <summary>
    /// Cache keys for Provider credentials and configuration.
    /// Used by RedisProviderCache for provider lookups.
    /// </summary>
    public static class Provider
    {
        /// <summary>Prefix for provider cache entries by ID</summary>
        public const string Prefix = "provider:";

        /// <summary>Prefix for provider cache entries by name (deprecated - only for cleanup)</summary>
        public const string NamePrefix = "provider:name:";

        /// <summary>Builds a cache key for a provider by ID</summary>
        /// <param name="providerId">The provider ID</param>
        /// <returns>Full cache key like "provider:123"</returns>
        public static string ById(int providerId) => $"{Prefix}{providerId}";
    }

    #endregion

    #region IP Filter Cache

    /// <summary>
    /// Cache keys for IP filtering rules.
    /// Used by RedisIpFilterCache for security filtering.
    /// </summary>
    public static class IpFilter
    {
        /// <summary>Key for global IP filter rules</summary>
        public const string GlobalFilters = "ipfilter:global";

        /// <summary>Prefix for virtual key-specific IP filters</summary>
        public const string VirtualKeyPrefix = "ipfilter:vkey:";

        /// <summary>Prefix for IP check result caching</summary>
        public const string CheckPrefix = "ipfilter:check:";

        /// <summary>Builds a cache key for virtual key IP filters</summary>
        /// <param name="virtualKeyId">The virtual key ID</param>
        /// <returns>Full cache key like "ipfilter:vkey:123"</returns>
        public static string ByVirtualKey(int virtualKeyId) => $"{VirtualKeyPrefix}{virtualKeyId}";

        /// <summary>Builds a cache key for IP check results</summary>
        /// <param name="ipAddress">The IP address being checked</param>
        /// <param name="virtualKeyId">Optional virtual key ID, or null for global check</param>
        /// <returns>Full cache key like "ipfilter:check:192.168.1.1:123" or "ipfilter:check:192.168.1.1:global"</returns>
        public static string CheckResult(string ipAddress, int? virtualKeyId) =>
            $"{CheckPrefix}{ipAddress}:{(virtualKeyId.HasValue ? virtualKeyId.Value.ToString() : "global")}";
    }

    #endregion

    #region Provider Tool Cache

    /// <summary>
    /// Cache keys for Provider Tool cost lookups.
    /// Used by RedisProviderToolCache for billing pipeline tool cost calculations.
    /// </summary>
    public static class ProviderTool
    {
        /// <summary>Prefix for provider tool cache entries by provider type</summary>
        public const string Prefix = "providertool:";

        /// <summary>Channel for tool invalidation notifications</summary>
        public const string InvalidationChannel = "ptool_invalidated";

        /// <summary>Builds a cache key for provider tools by provider type</summary>
        /// <param name="providerType">The provider type enum value</param>
        /// <returns>Full cache key like "providertool:Groq"</returns>
        public static string ByProvider(string providerType) => $"{Prefix}{providerType.ToLowerInvariant()}";
    }

    #endregion

    #region Ephemeral Key Cache

    /// <summary>
    /// Cache keys for ephemeral (temporary) API keys.
    /// Used by EphemeralKeyService and EphemeralMasterKeyService.
    /// </summary>
    public static class Ephemeral
    {
        /// <summary>Prefix for Gateway ephemeral keys</summary>
        public const string Prefix = "ephemeral:";

        /// <summary>Prefix for Admin master ephemeral keys</summary>
        public const string MasterPrefix = "ephemeral:master:";

        /// <summary>Token prefix for Gateway ephemeral keys (in the token itself)</summary>
        public const string TokenPrefix = "ek_";

        /// <summary>Token prefix for Admin master keys (in the token itself)</summary>
        public const string MasterTokenPrefix = "emk_";

        /// <summary>Builds a cache key for a Gateway ephemeral key</summary>
        /// <param name="token">The ephemeral key token</param>
        /// <returns>Full cache key like "ephemeral:ek_abc123"</returns>
        public static string ByToken(string token) => $"{Prefix}{token}";

        /// <summary>Builds a cache key for an Admin master ephemeral key</summary>
        /// <param name="token">The master key token</param>
        /// <returns>Full cache key like "ephemeral:master:emk_abc123"</returns>
        public static string MasterByToken(string token) => $"{MasterPrefix}{token}";
    }

    #endregion

    #region Provider Error Cache

    /// <summary>
    /// Cache keys for provider error tracking.
    /// Used by RedisErrorStore for error monitoring and key disabling.
    /// </summary>
    public static class ProviderError
    {
        /// <summary>Key for recent errors feed (global)</summary>
        public const string RecentFeed = "provider:errors:recent";

        /// <summary>Set of disabled key IDs considered by the balance reprobe worker.</summary>
        public const string DisabledKeys = "provider:errors:disabled_keys";

        /// <summary>Builds a key for fatal error data by credential key ID</summary>
        /// <param name="keyId">The provider key credential ID</param>
        /// <returns>Full key like "provider:errors:key:123:fatal"</returns>
        public static string FatalByKey(int keyId) => $"provider:errors:key:{keyId}:fatal";

        /// <summary>Builds a key for distinct fatal request IDs by key and error type.</summary>
        public static string FatalRequestsByType(int keyId, string errorType) =>
            $"provider:errors:key:{keyId}:fatal:{errorType.ToLowerInvariant()}:requests";

        /// <summary>Builds the short-lived guard key for a credential disable operation.</summary>
        public static string DisableGuard(int keyId) => $"provider:errors:key:{keyId}:disabling";

        /// <summary>Builds the distributed lock key for a balance reprobe.</summary>
        public static string ReprobeGuard(int keyId) => $"provider:errors:key:{keyId}:reprobing";

        /// <summary>Builds a key for warning data by credential key ID</summary>
        /// <param name="keyId">The provider key credential ID</param>
        /// <returns>Full key like "provider:errors:key:123:warnings"</returns>
        public static string WarningsByKey(int keyId) => $"provider:errors:key:{keyId}:warnings";

        /// <summary>Builds a key for provider-level error summary</summary>
        /// <param name="providerId">The provider ID</param>
        /// <returns>Full key like "provider:errors:provider:456:summary"</returns>
        public static string ProviderSummary(int providerId) => $"provider:errors:provider:{providerId}:summary";

        /// <summary>Builds a key for provider-level disabled keys set</summary>
        /// <param name="providerId">The provider ID</param>
        /// <returns>Full key like "provider:errors:provider:456:disabled_keys"</returns>
        public static string DisabledKeysByProvider(int providerId) => $"provider:errors:provider:{providerId}:disabled_keys";
    }

    #endregion

    #region Model Mapping Cache

    /// <summary>
    /// Cache keys for model-to-provider mapping lookups.
    /// Used by FusionModelProviderMappingService and ModelMappingCacheInvalidationHandler.
    /// </summary>
    public static class ModelMapping
    {
        /// <summary>Prefix for model mapping cache entries</summary>
        public const string Prefix = "model:mapping";

        /// <summary>Key for all mappings list cache</summary>
        public const string AllMappings = "model:mapping:all";

        /// <summary>Builds a cache key for mapping by model alias</summary>
        /// <param name="modelAlias">The model alias</param>
        /// <returns>Full cache key like "model:mapping:gpt-4"</returns>
        public static string ByAlias(string modelAlias) => $"model:mapping:{modelAlias}";

        /// <summary>Builds a cache key for mapping by ID</summary>
        /// <param name="id">The mapping ID</param>
        /// <returns>Full cache key like "model:mapping:id:123"</returns>
        public static string ById(int id) => $"model:mapping:id:{id}";
    }

    #endregion

    #region Media Progress Cache

    /// <summary>
    /// Cache keys for media generation progress tracking.
    /// Used by ImageGenerationProgressHandler and VideoGenerationProgressHandler.
    /// </summary>
    public static class MediaProgress
    {
        /// <summary>Prefix for image generation progress entries</summary>
        public const string ImagePrefix = "image_generation_progress_";

        /// <summary>Prefix for video generation progress entries</summary>
        public const string VideoPrefix = "video_generation_progress_";

        /// <summary>Builds a cache key for image generation progress</summary>
        /// <param name="taskId">The generation task ID</param>
        /// <returns>Full cache key like "image_generation_progress_abc123"</returns>
        public static string ImageProgress(string taskId) => $"{ImagePrefix}{taskId}";

        /// <summary>Builds a cache key for video generation progress</summary>
        /// <param name="requestId">The generation request ID</param>
        /// <returns>Full cache key like "video_generation_progress_abc123"</returns>
        public static string VideoProgress(string requestId) => $"{VideoPrefix}{requestId}";
    }

    #endregion

    #region Statistics Cache

    /// <summary>
    /// Cache keys for cache statistics tracking.
    /// Used by various Redis cache implementations for metrics collection.
    /// </summary>
    public static class Stats
    {
        /// <summary>Service name for Virtual Key cache statistics</summary>
        public const string VirtualKeyService = "vkey";

        /// <summary>Service name for Model Cost cache statistics</summary>
        public const string ModelCostService = "modelcost";

        /// <summary>Service name for Global Setting cache statistics</summary>
        public const string GlobalSettingService = "globalsetting";

        /// <summary>Service name for Provider cache statistics</summary>
        public const string ProviderService = "provider";

        /// <summary>Service name for IP Filter cache statistics</summary>
        public const string IpFilterService = "ipfilter";

        /// <summary>Service name for Provider Tool cache statistics</summary>
        public const string ProviderToolService = "providertool";

        /// <summary>Builds a hits counter key for a service</summary>
        /// <param name="service">The service name (use constants above)</param>
        /// <returns>Full key like "conduit:cache:modelcost:stats:hits"</returns>
        public static string Hits(string service) => $"conduit:cache:{service}:stats:hits";

        /// <summary>Builds a misses counter key for a service</summary>
        /// <param name="service">The service name (use constants above)</param>
        /// <returns>Full key like "conduit:cache:modelcost:stats:misses"</returns>
        public static string Misses(string service) => $"conduit:cache:{service}:stats:misses";

        /// <summary>Builds an invalidations counter key for a service</summary>
        /// <param name="service">The service name (use constants above)</param>
        /// <returns>Full key like "conduit:cache:modelcost:stats:invalidations"</returns>
        public static string Invalidations(string service) => $"conduit:cache:{service}:stats:invalidations";

        /// <summary>Builds a reset time key for a service</summary>
        /// <param name="service">The service name (use constants above)</param>
        /// <returns>Full key like "conduit:cache:modelcost:stats:reset_time"</returns>
        public static string ResetTime(string service) => $"conduit:cache:{service}:stats:reset_time";

        /// <summary>Builds a pattern matches counter key (model cost specific)</summary>
        /// <returns>Full key "conduit:cache:modelcost:stats:pattern_matches"</returns>
        public static string PatternMatches() => "conduit:cache:modelcost:stats:pattern_matches";

        /// <summary>Builds an auth hits counter key (global setting specific)</summary>
        /// <returns>Full key "conduit:cache:globalsetting:stats:auth_hits"</returns>
        public static string AuthHits() => "conduit:cache:globalsetting:stats:auth_hits";

        /// <summary>Builds an auth misses counter key (global setting specific)</summary>
        /// <returns>Full key "conduit:cache:globalsetting:stats:auth_misses"</returns>
        public static string AuthMisses() => "conduit:cache:globalsetting:stats:auth_misses";

        /// <summary>Builds an IP check counter key (IP filter specific)</summary>
        /// <returns>Full key "conduit:cache:ipfilter:stats:ip_checks"</returns>
        public static string IpChecks() => "conduit:cache:ipfilter:stats:ip_checks";

        // Legacy pattern for VirtualKeyCache (uses shorter path without service name)
        /// <summary>Legacy hits key for backward compatibility with VirtualKeyCache</summary>
        public const string VirtualKeyHits = "conduit:cache:stats:hits";

        /// <summary>Legacy misses key for backward compatibility with VirtualKeyCache</summary>
        public const string VirtualKeyMisses = "conduit:cache:stats:misses";

        /// <summary>Legacy invalidations key for backward compatibility with VirtualKeyCache</summary>
        public const string VirtualKeyInvalidations = "conduit:cache:stats:invalidations";

        /// <summary>Legacy reset time key for backward compatibility with VirtualKeyCache</summary>
        public const string VirtualKeyResetTime = "conduit:cache:stats:reset_time";
    }

    #endregion

    #region Distributed Lock Keys

    /// <summary>
    /// Cache keys for distributed lock operations (stampede prevention).
    /// Used by IDistributedCachePopulator for concurrent cache population.
    /// </summary>
    public static class Locks
    {
        /// <summary>Prefix for all distributed lock keys</summary>
        public const string Prefix = "populate:";

        /// <summary>Builds a lock key for model cost pattern population</summary>
        /// <param name="pattern">The model ID pattern</param>
        /// <returns>Full lock key like "populate:modelcost:pattern:gpt-4"</returns>
        public static string ModelCostPattern(string pattern) => $"{Prefix}modelcost:pattern:{pattern.ToLowerInvariant()}";

        /// <summary>Builds a lock key for model cost by model ID population</summary>
        /// <param name="modelId">The model ID</param>
        /// <returns>Full lock key like "populate:modelcost:modelid:gpt-4-turbo"</returns>
        public static string ModelCostModelId(string modelId) => $"{Prefix}modelcost:modelid:{modelId.ToLowerInvariant()}";

        /// <summary>Builds a lock key for provider credential population</summary>
        /// <param name="providerId">The provider ID</param>
        /// <returns>Full lock key like "populate:provider:123"</returns>
        public static string Provider(int providerId) => $"{Prefix}provider:{providerId}";
    }

    #endregion

    #region Analytics Cache

    /// <summary>
    /// Cache keys for analytics data (memory cache, not Redis).
    /// Used by AnalyticsService for dashboard data.
    /// </summary>
    public static class Analytics
    {
        /// <summary>Prefix for analytics summary data</summary>
        public const string SummaryPrefix = "analytics:summary:";

        /// <summary>Key for models analytics cache</summary>
        public const string Models = "analytics:models";

        /// <summary>Prefix for cost trend data</summary>
        public const string CostTrendPrefix = "analytics:cost:trend:";

        /// <summary>Builds a cache key for analytics summary by date range</summary>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date</param>
        /// <returns>Full cache key like "analytics:summary:20240101_20240131"</returns>
        public static string Summary(DateTime startDate, DateTime endDate) =>
            $"{SummaryPrefix}{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";

        /// <summary>Builds a cache key for cost trend data</summary>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date</param>
        /// <param name="granularity">Time granularity (hourly, daily, etc.)</param>
        /// <returns>Full cache key like "analytics:cost:trend:20240101_20240131_daily"</returns>
        public static string CostTrend(DateTime startDate, DateTime endDate, string granularity) =>
            $"{CostTrendPrefix}{startDate:yyyyMMdd}_{endDate:yyyyMMdd}_{granularity}";
    }

    #endregion

    #region Alert Management Cache

    /// <summary>
    /// Cache keys for alert management.
    /// Used by DistributedAlertManagementService.
    /// </summary>
    public static class AlertManagement
    {
        /// <summary>Prefix for alert history entries</summary>
        public const string HistoryPrefix = "alert_history";

        /// <summary>Prefix for alert locks (distributed locking)</summary>
        public const string LockPrefix = "alert_lock";
    }

    #endregion

}
