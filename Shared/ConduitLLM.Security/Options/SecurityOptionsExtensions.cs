using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Security.Options
{
    /// <summary>
    /// Extension methods for configuring security options
    /// </summary>
    public static class SecurityOptionsExtensions
    {
        /// <summary>
        /// Configures Admin security options from configuration
        /// </summary>
        public static IServiceCollection ConfigureAdminSecurityOptions(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<AdminSecurityOptions>(options =>
            {
                ConfigureBaseSecurityOptions(options, configuration, "CONDUIT_ADMIN_");

                // Retain the limit names previously published in .env.example. Canonical
                // names take precedence when both forms are supplied.
                options.RateLimiting.MaxRequests = GetConfigValue(configuration,
                    "CONDUIT_ADMIN_RATE_LIMIT_MAX_REQUESTS", options.RateLimiting.MaxRequests,
                    "CONDUIT_ADMIN_RATE_LIMITING_MAX_REQUESTS");
                options.RateLimiting.WindowSeconds = GetConfigValue(configuration,
                    "CONDUIT_ADMIN_RATE_LIMIT_WINDOW_SECONDS", options.RateLimiting.WindowSeconds,
                    "CONDUIT_ADMIN_RATE_LIMITING_WINDOW_SECONDS");
                options.FailedAuth.MaxAttempts = GetConfigValue(configuration,
                    "CONDUIT_ADMIN_MAX_FAILED_AUTH_ATTEMPTS", options.FailedAuth.MaxAttempts,
                    "CONDUIT_ADMIN_FAILED_AUTH_MAX_ATTEMPTS");
                options.FailedAuth.BanDurationMinutes = GetConfigValue(configuration,
                    "CONDUIT_ADMIN_AUTH_BAN_DURATION_MINUTES", options.FailedAuth.BanDurationMinutes,
                    "CONDUIT_ADMIN_FAILED_AUTH_BAN_DURATION_MINUTES");

                // API Authentication (Admin-specific)
                options.ApiAuth.ApiKeyHeader = configuration["CONDUIT_ADMIN_API_KEY_HEADER"]
                    ?? SecurityHeaderNames.ApiKey;

                var altHeaders = configuration["CONDUIT_ADMIN_API_KEY_ALT_HEADERS"];
                if (!string.IsNullOrWhiteSpace(altHeaders))
                {
                    options.ApiAuth.AlternativeHeaders = ParseCommaSeparatedList(altHeaders);
                }
            });

            return services;
        }

        /// <summary>
        /// Configures Gateway security options from configuration
        /// </summary>
        public static IServiceCollection ConfigureGatewaySecurityOptions(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<GatewaySecurityOptions>(options =>
            {
                ConfigureBaseSecurityOptions(options, configuration, "CONDUIT_CORE_");

                // Gateway-specific rate limiting (override base)
                options.RateLimiting.Enabled = GetConfigValue(configuration, "CONDUIT_CORE_RATE_LIMITING_ENABLED",
                    configuration.GetValue<bool>("CoreApi:Security:RateLimiting:Enabled", true));
                options.RateLimiting.MaxRequests = GetConfigValue(configuration, "CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS",
                    configuration.GetValue<int>("CoreApi:Security:RateLimiting:MaxRequests", 1000));
                options.RateLimiting.WindowSeconds = GetConfigValue(configuration, "CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS",
                    configuration.GetValue<int>("CoreApi:Security:RateLimiting:WindowSeconds", 60));

                var rateLimitExcluded = configuration["CONDUIT_CORE_RATE_LIMIT_EXCLUDED_PATHS"]
                    ?? configuration["CoreApi:Security:RateLimiting:ExcludedPaths"];
                if (!string.IsNullOrEmpty(rateLimitExcluded))
                {
                    options.RateLimiting.ExcludedPaths = ParseCommaSeparatedList(rateLimitExcluded);
                }

                // Bind discovery separately so its settings do not change the parent
                // limiter's established environment-variable precedence.
                var discoverySection = configuration.GetSection("CoreApi:Security:RateLimiting:Discovery");
                if (discoverySection.GetSection(nameof(DiscoveryRateLimitOptions.DiscoveryPaths)).Exists())
                {
                    // Configuration binding appends to initialized lists; configured
                    // route roots must replace the defaults instead.
                    options.RateLimiting.Discovery.DiscoveryPaths.Clear();
                }
                discoverySection.Bind(options.RateLimiting.Discovery);

                // Failed Auth (Gateway has TrackAcrossKeys)
                options.FailedAuth.TrackAcrossKeys = GetConfigValue(configuration, "CONDUIT_CORE_TRACK_FAILED_AUTH_ACROSS_KEYS",
                    configuration.GetValue<bool>("CoreApi:Security:FailedAuth:TrackAcrossKeys", true));

                // Virtual Key Options (Gateway-specific)
                options.VirtualKey.EnforceRateLimits = GetConfigValue(configuration, "CONDUIT_CORE_ENFORCE_VKEY_RATE_LIMITS",
                    configuration.GetValue<bool>("CoreApi:Security:VirtualKey:EnforceRateLimits", true));
                options.VirtualKey.EnforceBudgetLimits = GetConfigValue(configuration, "CONDUIT_CORE_ENFORCE_VKEY_BUDGETS",
                    configuration.GetValue<bool>("CoreApi:Security:VirtualKey:EnforceBudgetLimits", true));
                options.VirtualKey.EnforceModelRestrictions = GetConfigValue(configuration, "CONDUIT_CORE_ENFORCE_VKEY_MODELS",
                    configuration.GetValue<bool>("CoreApi:Security:VirtualKey:EnforceModelRestrictions", true));
                options.VirtualKey.ValidationCacheSeconds = GetConfigValue(configuration, "CONDUIT_CORE_VKEY_CACHE_SECONDS",
                    configuration.GetValue<int>("CoreApi:Security:VirtualKey:ValidationCacheSeconds", 60));

                var keyHeaders = configuration["CONDUIT_CORE_VKEY_HEADERS"]
                    ?? configuration["CoreApi:Security:VirtualKey:KeyHeaders"];
                if (!string.IsNullOrWhiteSpace(keyHeaders))
                {
                    options.VirtualKey.KeyHeaders = ParseCommaSeparatedList(keyHeaders);
                }
            });

            services.AddOptions<GatewaySecurityOptions>()
                .Validate(options => options.RateLimiting.Discovery.DiscoveryPaths != null
                    && options.RateLimiting.Discovery.DiscoveryPaths.All(path =>
                        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/')),
                    "Gateway discovery rate-limit paths must be nonblank absolute route roots starting with '/'.")
                .ValidateOnStart();

            return services;
        }

        /// <summary>
        /// Configures base security options shared between APIs
        /// </summary>
        private static void ConfigureBaseSecurityOptions(
            SecurityOptionsBase options,
            IConfiguration configuration,
            string envPrefix)
        {
            // IP Filtering
            var ipFilterEnabled = configuration[$"{envPrefix}IP_FILTERING_ENABLED"];
            if (!string.IsNullOrEmpty(ipFilterEnabled))
            {
                options.IpFiltering.Enabled = bool.Parse(ipFilterEnabled);
            }

            var ipFilterMode = configuration[$"{envPrefix}IP_FILTER_MODE"];
            if (!string.IsNullOrEmpty(ipFilterMode))
            {
                options.IpFiltering.Mode = ipFilterMode;
            }

            var allowPrivateIps = configuration[$"{envPrefix}IP_FILTER_ALLOW_PRIVATE"];
            if (!string.IsNullOrEmpty(allowPrivateIps))
            {
                options.IpFiltering.AllowPrivateIps = bool.Parse(allowPrivateIps);
            }

            var whitelist = configuration[$"{envPrefix}IP_FILTER_WHITELIST"];
            if (!string.IsNullOrWhiteSpace(whitelist))
            {
                options.IpFiltering.Whitelist = ParseCommaSeparatedList(whitelist);
            }

            var blacklist = configuration[$"{envPrefix}IP_FILTER_BLACKLIST"];
            if (!string.IsNullOrWhiteSpace(blacklist))
            {
                options.IpFiltering.Blacklist = ParseCommaSeparatedList(blacklist);
            }

            // Rate Limiting (base)
            var rateLimitEnabled = configuration[$"{envPrefix}RATE_LIMITING_ENABLED"];
            if (!string.IsNullOrEmpty(rateLimitEnabled))
            {
                options.RateLimiting.Enabled = bool.Parse(rateLimitEnabled);
            }

            var maxRequests = configuration[$"{envPrefix}RATE_LIMIT_MAX_REQUESTS"];
            if (!string.IsNullOrEmpty(maxRequests))
            {
                options.RateLimiting.MaxRequests = int.Parse(maxRequests);
            }

            var windowSeconds = configuration[$"{envPrefix}RATE_LIMIT_WINDOW_SECONDS"];
            if (!string.IsNullOrEmpty(windowSeconds))
            {
                options.RateLimiting.WindowSeconds = int.Parse(windowSeconds);
            }

            var rateLimitExcluded = configuration[$"{envPrefix}RATE_LIMIT_EXCLUDED_PATHS"];
            if (!string.IsNullOrWhiteSpace(rateLimitExcluded))
            {
                options.RateLimiting.ExcludedPaths = ParseCommaSeparatedList(rateLimitExcluded);
            }

            // Failed Authentication Protection
            var failedAuthEnabled = configuration[$"{envPrefix}IP_BANNING_ENABLED"];
            if (!string.IsNullOrEmpty(failedAuthEnabled))
            {
                options.FailedAuth.Enabled = bool.Parse(failedAuthEnabled);
            }

            var maxAttempts = configuration[$"{envPrefix}MAX_FAILED_AUTH_ATTEMPTS"];
            if (!string.IsNullOrEmpty(maxAttempts))
            {
                options.FailedAuth.MaxAttempts = int.Parse(maxAttempts);
            }

            var banDuration = configuration[$"{envPrefix}AUTH_BAN_DURATION_MINUTES"];
            if (!string.IsNullOrEmpty(banDuration))
            {
                options.FailedAuth.BanDurationMinutes = int.Parse(banDuration);
            }

            // Distributed Tracking (shared key)
            var useDistributed = configuration["CONDUIT_SECURITY_USE_DISTRIBUTED_TRACKING"];
            if (!string.IsNullOrEmpty(useDistributed))
            {
                options.UseDistributedTracking = bool.Parse(useDistributed);
            }

            // Security Headers
            ConfigureSecurityHeaders(options.Headers, configuration, envPrefix);
        }

        /// <summary>
        /// Configures security headers options
        /// </summary>
        private static void ConfigureSecurityHeaders(
            SecurityHeadersOptions headers,
            IConfiguration configuration,
            string envPrefix)
        {
            var xContentTypeOptions = configuration[$"{envPrefix}SECURITY_HEADERS_X_CONTENT_TYPE_OPTIONS_ENABLED"]
                ?? configuration[$"{envPrefix}SECURITY_HEADERS_CONTENT_TYPE"];
            if (!string.IsNullOrEmpty(xContentTypeOptions))
            {
                headers.XContentTypeOptions = bool.Parse(xContentTypeOptions);
            }

            var xXssProtection = configuration[$"{envPrefix}SECURITY_HEADERS_X_XSS_PROTECTION_ENABLED"]
                ?? configuration[$"{envPrefix}SECURITY_HEADERS_XSS"];
            if (!string.IsNullOrEmpty(xXssProtection))
            {
                headers.XXssProtection = bool.Parse(xXssProtection);
            }

            var hstsEnabled = configuration[$"{envPrefix}SECURITY_HEADERS_HSTS_ENABLED"];
            if (!string.IsNullOrEmpty(hstsEnabled))
            {
                headers.Hsts.Enabled = bool.Parse(hstsEnabled);
            }

            var hstsMaxAge = configuration[$"{envPrefix}SECURITY_HEADERS_HSTS_MAX_AGE"];
            if (!string.IsNullOrEmpty(hstsMaxAge))
            {
                headers.Hsts.MaxAge = int.Parse(hstsMaxAge);
            }
        }

        /// <summary>
        /// Helper to get config value with fallback
        /// </summary>
        private static T GetConfigValue<T>(
            IConfiguration configuration,
            string envKey,
            T fallback,
            string? legacyEnvKey = null) where T : struct
        {
            var value = configuration[envKey]
                ?? (legacyEnvKey == null ? null : configuration[legacyEnvKey]);
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            return (T)Convert.ChangeType(value, typeof(T));
        }

        /// <summary>
        /// Parses a comma-separated string into a list
        /// </summary>
        private static List<string> ParseCommaSeparatedList(string value)
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();
        }
    }
}
