using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;
using ConduitLLM.Security.Serialization;

namespace ConduitLLM.Security.Services
{
    /// <summary>
    /// Base class for security services shared between Admin and Gateway APIs.
    /// Provides common IP banning, rate limiting, IP filtering, and failed auth tracking.
    /// </summary>
    public abstract class SecurityServiceBase : Interfaces.ISecurityService
    {
        protected readonly ILogger Logger;
        protected readonly IMemoryCache MemoryCache;
        protected readonly IDistributedCache? DistributedCache;

        /// <summary>
        /// Atomic counter behind every fixed-window limiter in this service. Read-then-write
        /// counting undercounts under exactly the concurrency a rate limiter exists to handle.
        /// </summary>
        protected readonly IFixedWindowCounter RateLimitCounter;
        protected readonly IFailedAuthTracker FailedAuthTracker;

        // Fixed-window keys include the API service name.
        protected const string RateLimitPrefix = "rate_limit:";
        // Legacy failed-auth keys use each API's configured cache namespace. The atomic
        // tracker scopes its versioned keys explicitly by service name and IP address.
        protected const string FailedLoginPrefix = "failed_login:";
        protected const string BanPrefix = "ban:";

        /// <summary>
        /// Service identifier for cache tracking (e.g., "admin-api", "core-api")
        /// </summary>
        protected abstract string ServiceName { get; }

        /// <summary>
        /// The security options for this service
        /// </summary>
        protected abstract SecurityOptionsBase Options { get; }

        protected SecurityServiceBase(
            ILogger logger,
            IMemoryCache memoryCache,
            IDistributedCache? distributedCache,
            IFixedWindowCounter? rateLimitCounter = null,
            IFailedAuthTracker? failedAuthTracker = null)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            MemoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            DistributedCache = distributedCache;

            // Without a shared store there is no cross-instance limit to make atomic, so the
            // in-process counter is the honest fallback rather than a degraded distributed one.
            RateLimitCounter = rateLimitCounter ?? new MemoryFixedWindowCounter(memoryCache);
            FailedAuthTracker = failedAuthTracker ?? new MemoryFailedAuthTracker(memoryCache);
        }

        /// <inheritdoc/>
        public abstract Task<SecurityCheckResult> IsRequestAllowedAsync(HttpContext context);

        /// <inheritdoc/>
        public virtual async Task RecordFailedAuthAsync(string ipAddress, string attemptedKey = "")
        {
            if (!Options.FailedAuth.Enabled)
            {
                Logger.LogDebug("Failed auth recording is disabled via configuration for IP {IpAddress}", ipAddress);
                return;
            }

            var maskedKey = MaskKey(attemptedKey);
            var recorded = await FailedAuthTracker.RecordAsync(ServiceName, ipAddress,
                Options.FailedAuth.MaxAttempts, TimeSpan.FromMinutes(Options.FailedAuth.BanDurationMinutes), maskedKey);
            Logger.LogWarning(
                "Failed authentication attempt {Attempts}/{MaxAttempts} for IP {IpAddress}{KeyInfo}",
                recorded.Attempts, Options.FailedAuth.MaxAttempts, ipAddress,
                string.IsNullOrEmpty(maskedKey) ? "" : $" with key {maskedKey}");
            if (recorded.NewBan && recorded.Ban is { } banInfo)
            {
                // The atomic tracker is authoritative. Mirror the first ban for legacy readers
                // during rollout, with its original absolute deadline rather than a fresh TTL.
                try
                {
                    var remaining = banInfo.BannedUntil - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        await SetCacheValueAsync($"{BanPrefix}{ipAddress}", banInfo, remaining,
                            SecurityCacheJsonContext.Default.BannedIpInfo);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "Could not mirror failed-auth ban for legacy readers at {IpAddress}", ipAddress);
                }
                Logger.LogWarning("IP {IpAddress} has been banned after {Attempts} failed authentication attempts",
                    ipAddress, recorded.Attempts);
            }
        }

        /// <inheritdoc/>
        public virtual async Task ClearFailedAuthAttemptsAsync(string ipAddress)
        {
            if (!Options.FailedAuth.Enabled) return;
            await FailedAuthTracker.ClearAttemptsAsync(ServiceName, ipAddress);
            var key = $"{FailedLoginPrefix}{ipAddress}";
            try { await RemoveCacheValueAsync(key); }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "Could not clear legacy failed-auth attempts at {IpAddress}", ipAddress);
            }
            Logger.LogDebug("Cleared failed authentication attempts for IP {IpAddress}", ipAddress);
        }

        /// <inheritdoc/>
        public virtual async Task<bool> IsIpBannedAsync(string ipAddress)
        {
            if (!Options.FailedAuth.Enabled)
            {
                return false;
            }

            if (await FailedAuthTracker.GetBanAsync(ServiceName, ipAddress) is not null) return true;
            // Keep live legacy bans visible until their recorded deadline. Reads never renew them.
            try
            {
                var legacy = await GetCacheObjectAsync($"{BanPrefix}{ipAddress}", SecurityCacheJsonContext.Default.BannedIpInfo);
                return legacy?.BannedUntil > DateTime.UtcNow;
            }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "Legacy failed-auth ban lookup unavailable for {IpAddress}", ipAddress);
                return false;
            }
        }

        /// <summary>
        /// Checks IP-based rate limiting
        /// </summary>
        /// <remarks>
        /// Check and increment happen in one atomic operation. Splitting them let concurrent
        /// requests — across instances or within one — all read the same pre-increment count and
        /// be admitted together, so the effective ceiling was the limit times the request
        /// concurrency rather than the limit.
        /// </remarks>
        protected async Task<SecurityCheckResult> CheckIpRateLimitAsync(string ipAddress)
        {
            var key = $"{RateLimitPrefix}{ServiceName}:{ipAddress}";
            var requestCount = await RateLimitCounter.IncrementAsync(key, Options.RateLimiting.WindowSeconds);

            if (requestCount > Options.RateLimiting.MaxRequests)
            {
                Logger.LogWarning("Rate limit exceeded for IP {IpAddress}: {Count} requests in {Window} seconds",
                    ipAddress, requestCount, Options.RateLimiting.WindowSeconds);

                var resetsAt = await RateLimitCounter.GetResetAsync(key)
                    ?? DateTime.UtcNow.AddSeconds(Options.RateLimiting.WindowSeconds);

                return SecurityCheckResult.RateLimited(
                    "Rate limit exceeded",
                    resetsAt,
                    Options.RateLimiting.MaxRequests,
                    remaining: 0,
                    scope: "ip");
            }

            return SecurityCheckResult.Allowed();
        }

        /// <summary>
        /// Checks IP filtering rules (whitelist/blacklist + database).
        /// Subclasses must provide the database check via <see cref="CheckDatabaseIpFilterAsync"/>.
        /// </summary>
        protected async Task<SecurityCheckResult> CheckIpFilterAsync(string ipAddress)
        {
            // Check if it's a private IP and we allow private IPs
            if (Options.IpFiltering.AllowPrivateIps && IpAddressHelper.IsPrivateIp(ipAddress))
            {
                Logger.LogDebug("Private/Intranet IP {IpAddress} is automatically allowed", ipAddress);
                return SecurityCheckResult.Allowed();
            }

            // Check environment variable based filters
            var isInWhitelist = Options.IpFiltering.Whitelist.Any(rule => IpAddressHelper.IsIpInRange(ipAddress, rule));
            var isInBlacklist = Options.IpFiltering.Blacklist.Any(rule => IpAddressHelper.IsIpInRange(ipAddress, rule));

            var isAllowed = Options.IpFiltering.Mode.Equals("restrictive", StringComparison.OrdinalIgnoreCase)
                ? isInWhitelist && !isInBlacklist
                : !isInBlacklist;

            if (!isAllowed)
            {
                Logger.LogWarning("IP {IpAddress} blocked by IP filter rules", ipAddress);
                return SecurityCheckResult.Denied("IP address not allowed");
            }

            // Check database-based IP filters (service-specific implementation)
            return await CheckDatabaseIpFilterAsync(ipAddress);
        }

        /// <summary>
        /// Checks database-based IP filters. Override in derived classes to use the appropriate IP filter service.
        /// </summary>
        protected virtual Task<SecurityCheckResult> CheckDatabaseIpFilterAsync(string ipAddress)
        {
            return Task.FromResult(SecurityCheckResult.Allowed());
        }

        /// <summary>
        /// Checks route segment exclusions without matching unrelated names.
        /// </summary>
        protected static bool IsPathExcluded(string path, List<string> excludedPaths)
        {
            return excludedPaths.Any(excluded =>
                !string.IsNullOrWhiteSpace(excluded) &&
                path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase) &&
                // Keep a configured trailing slash descendants-only: '/api/'
                // must not newly exempt the separate '/api' route.
                (excluded.EndsWith('/') || path.Length == excluded.Length || path[excluded.Length] == '/'));
        }

        /// <summary>
        /// Gets the client IP address from the request
        /// </summary>
        protected static string GetClientIpAddress(HttpContext context)
        {
            return IpAddressHelper.GetClientIpAddress(context);
        }

        // ─── Cache Helpers ──────────────────────────────────────────────

        /// <summary>
        /// Gets a reference type value from distributed or memory cache
        /// </summary>
        protected async Task<T?> GetCacheObjectAsync<T>(
            string key,
            JsonTypeInfo<T> jsonTypeInfo)
            where T : class
        {
            if (Options.UseDistributedTracking && DistributedCache != null)
            {
                var cachedValue = await DistributedCache.GetStringAsync(key);
                if (!string.IsNullOrEmpty(cachedValue))
                {
                    try
                    {
                        return JsonSerializer.Deserialize(cachedValue, jsonTypeInfo);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            else
            {
                return MemoryCache.Get<T>(key);
            }

            return null;
        }

        /// <summary>
        /// Sets a value in distributed or memory cache
        /// </summary>
        protected async Task SetCacheValueAsync<T>(
            string key,
            T value,
            TimeSpan expiration,
            JsonTypeInfo<T> jsonTypeInfo,
            bool sliding = false)
        {
            if (Options.UseDistributedTracking && DistributedCache != null)
            {
                var options = new DistributedCacheEntryOptions();
                if (sliding)
                    options.SlidingExpiration = expiration;
                else
                    options.AbsoluteExpirationRelativeToNow = expiration;

                await DistributedCache.SetStringAsync(
                    key,
                    JsonSerializer.Serialize(value, jsonTypeInfo),
                    options);
            }
            else
            {
                if (sliding)
                    MemoryCache.Set(key, value, new MemoryCacheEntryOptions { SlidingExpiration = expiration });
                else
                    MemoryCache.Set(key, value, expiration);
            }
        }

        /// <summary>
        /// Removes a value from distributed or memory cache
        /// </summary>
        protected async Task RemoveCacheValueAsync(string key)
        {
            if (Options.UseDistributedTracking && DistributedCache != null)
            {
                await DistributedCache.RemoveAsync(key);
            }
            else
            {
                MemoryCache.Remove(key);
            }
        }

        private static string MaskKey(string key)
        {
            // Empty stays "" (callers use it to omit the " with key ..." log suffix entirely).
            if (string.IsNullOrEmpty(key)) return "";
            return ConduitLLM.Core.Utilities.SpanHelper.MaskSecret(key);
        }
    }
}
