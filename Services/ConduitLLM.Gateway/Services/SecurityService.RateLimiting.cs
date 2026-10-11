using ConduitLLM.Security.Models;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// IP-based rate limiting with discovery-specific overrides for the Gateway.
    /// Virtual Key (RPM/RPD) rate limiting is enforced separately by
    /// <see cref="ConduitLLM.Gateway.Middleware.VirtualKeyRateLimitMiddleware"/>.
    /// </summary>
    public partial class SecurityService
    {
        /// <summary>
        /// Checks IP rate limiting with discovery-specific overrides
        /// </summary>
        private async Task<SecurityCheckResult> CheckIpRateLimitWithDiscoveryAsync(string ipAddress, string path)
        {
            // Check discovery-specific rate limiting first
            if (_options.RateLimiting.Discovery.Enabled && IsDiscoveryPath(path))
            {
                var discoveryResult = await CheckDiscoveryRateLimitAsync(ipAddress, path);
                if (!discoveryResult.IsAllowed)
                {
                    return discoveryResult;
                }
            }

            // Fall through to base IP rate limiting
            return await CheckIpRateLimitAsync(ipAddress);
        }

        private bool IsDiscoveryPath(string path)
        {
            var requestPath = new PathString(path);

            return _options.RateLimiting.Discovery.DiscoveryPaths
                .Any(discoveryPath => requestPath.StartsWithSegments(
                    discoveryPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }

        /// <remarks>
        /// The counter advances atomically for every request, admitted or not. Counting rejected
        /// attempts is deliberate for an abuse limiter, and it cannot extend a client's own block:
        /// the window's expiry is fixed when it opens, not renewed per request.
        /// </remarks>
        private async Task<SecurityCheckResult> CheckDiscoveryRateLimitAsync(string ipAddress, string path)
        {
            var discoveryKey = $"{RateLimitPrefix}discovery:{ipAddress}";

            var discoveryCount = await RateLimitCounter.IncrementAsync(
                discoveryKey, _options.RateLimiting.Discovery.WindowSeconds);

            if (discoveryCount > _options.RateLimiting.Discovery.MaxRequests)
            {
                Logger.LogWarning("Discovery rate limit exceeded for {IpAddress}: {Count} requests in {Window} seconds for path {Path}",
                    ipAddress, discoveryCount, _options.RateLimiting.Discovery.WindowSeconds, path);

                var resetsAt = await RateLimitCounter.GetResetAsync(discoveryKey)
                    ?? DateTime.UtcNow.AddSeconds(_options.RateLimiting.Discovery.WindowSeconds);

                return SecurityCheckResult.RateLimited(
                    $"Discovery rate limit exceeded for path {path}",
                    resetsAt,
                    _options.RateLimiting.Discovery.MaxRequests,
                    Math.Max(0, _options.RateLimiting.Discovery.MaxRequests - discoveryCount),
                    "discovery");
            }

            // Check per-model capability rate limiting
            if (path.Contains("/capabilities/", StringComparison.OrdinalIgnoreCase))
            {
                var modelMatch = ExtractModelFromPath(path);
                if (!string.IsNullOrEmpty(modelMatch))
                {
                    var capabilityResult = await CheckModelCapabilityRateLimitAsync(ipAddress, modelMatch);
                    if (!capabilityResult.IsAllowed)
                    {
                        return capabilityResult;
                    }
                }
            }

            return SecurityCheckResult.Allowed();
        }

        private async Task<SecurityCheckResult> CheckModelCapabilityRateLimitAsync(string ipAddress, string modelName)
        {
            var capabilityKey = $"{RateLimitPrefix}capability:{ipAddress}:{modelName}";

            var capabilityCount = await RateLimitCounter.IncrementAsync(
                capabilityKey, _options.RateLimiting.Discovery.CapabilityCheckWindowSeconds);

            if (capabilityCount > _options.RateLimiting.Discovery.MaxCapabilityChecksPerModel)
            {
                Logger.LogWarning("Model capability rate limit exceeded for {IpAddress} and model {Model}: {Count} requests in {Window} seconds",
                    ipAddress, modelName, capabilityCount, _options.RateLimiting.Discovery.CapabilityCheckWindowSeconds);

                var resetsAt = await RateLimitCounter.GetResetAsync(capabilityKey)
                    ?? DateTime.UtcNow.AddSeconds(_options.RateLimiting.Discovery.CapabilityCheckWindowSeconds);

                return SecurityCheckResult.RateLimited(
                    $"Capability check rate limit exceeded for model {modelName}",
                    resetsAt,
                    _options.RateLimiting.Discovery.MaxCapabilityChecksPerModel,
                    Math.Max(0, _options.RateLimiting.Discovery.MaxCapabilityChecksPerModel - capabilityCount),
                    "model-capability",
                    new Dictionary<string, string>
                    {
                        ["X-RateLimit-Model"] = modelName
                    });
            }

            return SecurityCheckResult.Allowed();
        }

        private static string ExtractModelFromPath(string path)
        {
            try
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segments.Length - 2; i++)
                {
                    if (segments[i].Equals("models", StringComparison.OrdinalIgnoreCase) &&
                        i + 2 < segments.Length &&
                        segments[i + 2].Equals("capabilities", StringComparison.OrdinalIgnoreCase))
                    {
                        return segments[i + 1];
                    }
                }
                return "";
            }
            catch
            {
                return "";
            }
        }
    }
}
