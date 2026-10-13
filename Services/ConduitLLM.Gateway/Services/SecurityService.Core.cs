using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Security.Interfaces;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;
using ConduitLLM.Security.Services;

using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Gateway-specific security service interface.
    /// Virtual Key rate limiting is enforced separately by VirtualKeyRateLimitMiddleware.
    /// </summary>
    public interface IGatewaySecurityService : ConduitLLM.Security.Interfaces.ISecurityService
    {
    }

    /// <summary>
    /// Implementation of security service for Gateway API.
    /// Handles authentication-related state (failed-auth tracking, IP bans, IP filtering,
    /// discovery-specific rate limits). Virtual Key rate limits are enforced by
    /// <see cref="ConduitLLM.Gateway.Middleware.VirtualKeyRateLimitMiddleware"/>.
    /// </summary>
    public partial class SecurityService : SecurityServiceBase, IGatewaySecurityService
    {
        private readonly GatewaySecurityOptions _options;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;

        /// <inheritdoc/>
        protected override string ServiceName => "core-api";

        /// <inheritdoc/>
        protected override SecurityOptionsBase Options => _options;

        /// <summary>
        /// Initializes a new instance of the Gateway SecurityService
        /// </summary>
        public SecurityService(
            IOptions<GatewaySecurityOptions> options,
            IConfiguration configuration,
            ILogger<SecurityService> logger,
            IMemoryCache memoryCache,
            IServiceProvider serviceProvider)
            : base(
                logger,
                memoryCache,
                serviceProvider.GetService<IDistributedCache>(),
                BuildRateLimitCounter(options.Value, serviceProvider, logger),
                FailedAuthTrackerFactory.Create(options.Value, serviceProvider, memoryCache, logger))
        {
            _options = options.Value;
            _configuration = configuration;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Picks the counter the fixed-window limiters run on.
        /// </summary>
        /// <remarks>
        /// The distributed path is gated on Redis actually being present rather than assumed:
        /// <c>IDistributedCache</c> is not necessarily Redis, and only Redis can do the atomic
        /// increment. Where it is absent the in-process counter takes over, which is correct for
        /// a single instance and is all a Redis-less deployment could ever have had.
        /// </remarks>
        private static IFixedWindowCounter? BuildRateLimitCounter(
            GatewaySecurityOptions options,
            IServiceProvider serviceProvider,
            ILogger logger)
        {
            if (!options.UseDistributedTracking)
            {
                return null;
            }

            var redis = serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis is null)
            {
                logger.LogWarning(
                    "Distributed rate-limit tracking is enabled but no Redis connection is registered; " +
                    "IP and discovery limits will be counted per instance only");
                return null;
            }

            return new RedisFixedWindowCounter(redis, logger);
        }

        /// <inheritdoc/>
        public override async Task<SecurityCheckResult> IsRequestAllowedAsync(HttpContext context)
        {
            var clientIp = GetClientIpAddress(context);
            var path = context.Request.Path.Value ?? "";

            // Skip security checks for excluded paths
            if (IsPathExcluded(path, new List<string> { "/health", "/metrics" }))
            {
                return SecurityCheckResult.Allowed();
            }

            // Check if authentication failed (set by VirtualKeyAuthenticationHandler)
            if (context.Items.ContainsKey("FailedAuth") && context.Items["FailedAuth"] is bool failedAuth && failedAuth)
            {
                var attemptedKey = context.Items["AttemptedKey"] as string ?? "unknown";
                await RecordFailedAuthAsync(clientIp, attemptedKey);
            }

            // Check if IP is banned
            if (await IsIpBannedAsync(clientIp))
            {
                return SecurityCheckResult.Denied("IP is banned due to excessive failed authentication attempts");
            }

            // If authentication succeeded, clear failed attempts
            if (context.Items.ContainsKey("AuthSuccess") && context.Items["AuthSuccess"] is bool authSuccess && authSuccess)
            {
                await ClearFailedAuthAttemptsAsync(clientIp);
            }

            // Check IP-based rate limiting
            if (_options.RateLimiting.Enabled && !IsPathExcluded(path, _options.RateLimiting.ExcludedPaths))
            {
                var rateLimitResult = await CheckIpRateLimitWithDiscoveryAsync(clientIp, path);
                if (!rateLimitResult.IsAllowed)
                {
                    return rateLimitResult;
                }
            }

            // Check IP filtering. The DB-persisted "IpFilter:Enabled" toggle (managed from the WebAdmin
            // UI) is authoritative when present; the env-bound option is the bootstrap fallback.
            if (await IsIpFilteringEnabledAsync() && !IsPathExcluded(path, _options.IpFiltering.ExcludedPaths))
            {
                var ipFilterResult = await CheckIpFilterAsync(clientIp);
                if (!ipFilterResult.IsAllowed)
                {
                    return ipFilterResult;
                }

                // Per-virtual-key IP filtering: the key's own allow/deny list further restricts access.
                // The authenticated key entity is stashed in context by VirtualKeyAuthenticationMiddleware.
                if (context.Items.TryGetValue("VirtualKeyEntity", out var vkObj) && vkObj is VirtualKey virtualKey)
                {
                    var perKeyResult = await CheckVirtualKeyIpFilterAsync(clientIp, virtualKey.Id);
                    if (!perKeyResult.IsAllowed)
                    {
                        return perKeyResult;
                    }
                }
            }

            // Note: Virtual Key rate limits (RPM/RPD) are enforced by
            // VirtualKeyRateLimitMiddleware, which runs after authentication and uses
            // the Redis-backed sliding-window IVirtualKeyRateLimitService.

            return SecurityCheckResult.Allowed();
        }

        /// <inheritdoc/>
        protected override async Task<SecurityCheckResult> CheckDatabaseIpFilterAsync(string ipAddress)
        {
            using var scope = _serviceProvider.CreateScope();
            var ipFilterService = scope.ServiceProvider.GetRequiredService<Interfaces.IIpFilterService>();
            var isAllowedByDb = await ipFilterService.IsIpAllowedAsync(ipAddress);

            if (!isAllowedByDb)
            {
                Logger.LogWarning("IP {IpAddress} blocked by database IP filter", ipAddress);
                return SecurityCheckResult.Denied("IP address not allowed");
            }

            return SecurityCheckResult.Allowed();
        }

        /// <summary>
        /// Applies the given virtual key's per-key IP allow/deny rules (in addition to global filtering).
        /// </summary>
        private async Task<SecurityCheckResult> CheckVirtualKeyIpFilterAsync(string ipAddress, int virtualKeyId)
        {
            using var scope = _serviceProvider.CreateScope();
            var ipFilterService = scope.ServiceProvider.GetRequiredService<Interfaces.IIpFilterService>();
            var isAllowed = await ipFilterService.IsIpAllowedForVirtualKeyAsync(ipAddress, virtualKeyId);

            if (!isAllowed)
            {
                Logger.LogWarning("IP {IpAddress} blocked by per-key IP filter for key {VirtualKeyId}",
                    ipAddress, virtualKeyId);
                return SecurityCheckResult.Denied("IP address not allowed for this key");
            }

            return SecurityCheckResult.Allowed();
        }

        /// <summary>
        /// Whether IP filtering is enabled. The DB-persisted "IpFilter:Enabled" setting (managed via the
        /// WebAdmin UI, cached in-memory by GlobalSettingsCacheService and invalidated on change) is
        /// authoritative when present; otherwise falls back to the env-bound option. This makes the UI
        /// toggle actually govern the Gateway data plane without a restart.
        /// </summary>
        private async Task<bool> IsIpFilteringEnabledAsync()
        {
            var globalSettings = _serviceProvider.GetService<IGlobalSettingsCacheService>();
            if (globalSettings != null)
            {
                var value = await globalSettings.GetSettingValueAsync("IpFilter:Enabled");
                if (value != null && bool.TryParse(value, out var enabled))
                {
                    return enabled;
                }
            }

            return _options.IpFiltering.Enabled;
        }
    }
}
