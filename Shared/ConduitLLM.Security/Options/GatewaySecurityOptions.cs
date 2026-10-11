namespace ConduitLLM.Security.Options
{
    /// <summary>
    /// Security configuration options specific to the Gateway API
    /// </summary>
    public class GatewaySecurityOptions : SecurityOptionsBase
    {
        /// <summary>
        /// Virtual Key specific options
        /// </summary>
        public VirtualKeyOptions VirtualKey { get; set; } = new();

        /// <summary>
        /// Gateway-specific rate limiting with discovery options, backed by the same
        /// instance used by shared security services through SecurityOptionsBase.
        /// </summary>
        public new GatewayRateLimitingOptions RateLimiting
        {
            get => (GatewayRateLimitingOptions)base.RateLimiting;
            set => base.RateLimiting = value;
        }

        /// <summary>
        /// Initializes a new instance with Gateway-specific defaults
        /// </summary>
        public GatewaySecurityOptions()
        {
            RateLimiting = new GatewayRateLimitingOptions();

            // Gateway API defaults - different from Admin
            IpFiltering.Enabled = true; // Gateway exposed to external traffic
            IpFiltering.ExcludedPaths = new List<string> { "/health", "/metrics" };

            Headers.XXssProtection = false; // Not needed for API-only service

            FailedAuth.MaxAttempts = 10; // More lenient for virtual keys
        }
    }

    /// <summary>
    /// Gateway-specific rate limiting options with discovery support
    /// </summary>
    public class GatewayRateLimitingOptions : RateLimitingOptionsBase
    {
        /// <summary>
        /// Discovery-specific rate limiting configuration
        /// </summary>
        public DiscoveryRateLimitOptions Discovery { get; set; } = new();

        /// <summary>
        /// Initializes with Gateway-specific defaults
        /// </summary>
        public GatewayRateLimitingOptions()
        {
            Enabled = true;
            MaxRequests = 1000;
            WindowSeconds = 60;
            ExcludedPaths = new List<string> { "/health", "/metrics", "/swagger" };
        }
    }

    /// <summary>
    /// Discovery API specific rate limiting configuration
    /// </summary>
    public class DiscoveryRateLimitOptions
    {
        /// <summary>
        /// Whether discovery-specific rate limiting is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum discovery requests per IP per window
        /// </summary>
        public int MaxRequests { get; set; } = 500;

        /// <summary>
        /// Time window in seconds for discovery requests
        /// </summary>
        public int WindowSeconds { get; set; } = 300; // 5 minutes

        /// <summary>
        /// Paths that count towards discovery rate limits
        /// </summary>
        public List<string> DiscoveryPaths { get; set; } = new()
        {
            "/v1/conduit/discovery/",
            "/v1/models/",
            "/capabilities/"
        };

        /// <summary>
        /// Maximum capability check requests per model per IP per window
        /// </summary>
        public int MaxCapabilityChecksPerModel { get; set; } = 20;

        /// <summary>
        /// Time window for per-model capability checks in seconds
        /// </summary>
        public int CapabilityCheckWindowSeconds { get; set; } = 600; // 10 minutes
    }

    /// <summary>
    /// Virtual Key specific options
    /// </summary>
    public class VirtualKeyOptions
    {
        /// <summary>
        /// Whether to enforce Virtual Key rate limits from database
        /// </summary>
        public bool EnforceRateLimits { get; set; } = true;

        /// <summary>
        /// Whether to enforce Virtual Key budget limits
        /// </summary>
        public bool EnforceBudgetLimits { get; set; } = true;

        /// <summary>
        /// Whether to enforce model access restrictions
        /// </summary>
        public bool EnforceModelRestrictions { get; set; } = true;

        /// <summary>
        /// Cache duration for Virtual Key validation in seconds
        /// </summary>
        public int ValidationCacheSeconds { get; set; } = 60;

        /// <summary>
        /// Headers to check for Virtual Key (in order of preference)
        /// </summary>
        public List<string> KeyHeaders { get; set; } = new()
        {
            SecurityHeaderNames.Authorization,
            "api-key",
            SecurityHeaderNames.ApiKey,
            SecurityHeaderNames.VirtualKey
        };
    }
}
