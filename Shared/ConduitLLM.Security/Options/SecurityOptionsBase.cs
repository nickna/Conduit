namespace ConduitLLM.Security.Options
{
    /// <summary>
    /// Base security configuration options shared between Admin and Gateway APIs
    /// </summary>
    public class SecurityOptionsBase
    {
        /// <summary>
        /// IP filtering configuration
        /// </summary>
        public IpFilteringOptions IpFiltering { get; set; } = new();

        /// <summary>
        /// Rate limiting configuration
        /// </summary>
        public RateLimitingOptionsBase RateLimiting { get; set; } = new();

        /// <summary>
        /// Failed authentication protection configuration
        /// </summary>
        public FailedAuthOptionsBase FailedAuth { get; set; } = new();

        /// <summary>
        /// Security headers configuration
        /// </summary>
        public SecurityHeadersOptions Headers { get; set; } = new();

        /// <summary>
        /// Whether to use distributed (Redis) tracking for security features
        /// </summary>
        public bool UseDistributedTracking { get; set; } = true;
    }

    /// <summary>
    /// IP filtering options - identical for both APIs
    /// </summary>
    public class IpFilteringOptions
    {
        /// <summary>
        /// Whether IP filtering is enabled
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Filter mode: "permissive" (blacklist) or "restrictive" (whitelist)
        /// </summary>
        public string Mode { get; set; } = "permissive";

        /// <summary>
        /// Whether to allow private/intranet IPs
        /// </summary>
        public bool AllowPrivateIps { get; set; } = true;

        /// <summary>
        /// List of whitelisted IPs or CIDR ranges
        /// </summary>
        public List<string> Whitelist { get; set; } = new();

        /// <summary>
        /// List of blacklisted IPs or CIDR ranges
        /// </summary>
        public List<string> Blacklist { get; set; } = new();

        /// <summary>
        /// Paths excluded from IP filtering
        /// </summary>
        public List<string> ExcludedPaths { get; set; } = new() { "/health" };
    }

    /// <summary>
    /// Base rate limiting options - shared properties
    /// </summary>
    public class RateLimitingOptionsBase
    {
        /// <summary>
        /// Whether rate limiting is enabled
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Maximum number of requests allowed per window
        /// </summary>
        public int MaxRequests { get; set; } = 100;

        /// <summary>
        /// Time window in seconds
        /// </summary>
        public int WindowSeconds { get; set; } = 60;

        /// <summary>
        /// Paths excluded from rate limiting
        /// </summary>
        public List<string> ExcludedPaths { get; set; } = new() { "/health" };
    }

    /// <summary>
    /// Base failed authentication protection options
    /// </summary>
    public class FailedAuthOptionsBase
    {
        /// <summary>
        /// Whether failed auth protection is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum number of failed authentication attempts before banning
        /// </summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>
        /// Duration in minutes for which an IP is banned
        /// </summary>
        public int BanDurationMinutes { get; set; } = 30;

        /// <summary>
        /// Whether to track failed attempts across all keys (Gateway-specific, but safe to include in base)
        /// </summary>
        public bool TrackAcrossKeys { get; set; } = true;
    }

    /// <summary>
    /// Security headers options - identical for both APIs
    /// </summary>
    public class SecurityHeadersOptions
    {
        /// <summary>
        /// Whether security middleware may change response headers and the host
        /// suppresses Kestrel's Server header. When disabled, individual header
        /// settings are ignored and Kestrel's configured behavior is preserved.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// X-Content-Type-Options header
        /// </summary>
        public bool XContentTypeOptions { get; set; } = true;

        /// <summary>
        /// X-XSS-Protection header
        /// </summary>
        public bool XXssProtection { get; set; } = true;

        /// <summary>
        /// Strict-Transport-Security options
        /// </summary>
        public HstsOptions Hsts { get; set; } = new();

        /// <summary>
        /// Custom headers to add
        /// </summary>
        public Dictionary<string, string> CustomHeaders { get; set; } = new();
    }

    /// <summary>
    /// HSTS (HTTP Strict Transport Security) options
    /// </summary>
    public class HstsOptions
    {
        /// <summary>
        /// Whether HSTS is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Max age in seconds
        /// </summary>
        public int MaxAge { get; set; } = 31536000; // 1 year
    }
}
