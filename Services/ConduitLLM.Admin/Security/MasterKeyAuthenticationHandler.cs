using ConduitLLM.Core.Extensions;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ConduitLLM.Admin.Metrics;
using ConduitLLM.Admin.Services;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Security.Cryptography;
using ConduitLLM.Security.Options;

namespace ConduitLLM.Admin.Security
{
    /// <summary>
    /// Authentication handler that validates the master key from request headers
    /// </summary>
    public class MasterKeyAuthenticationHandler : AuthenticationHandler<MasterKeyAuthenticationSchemeOptions>
    {
        private readonly string? _masterKey;
        private readonly IEphemeralMasterKeyService _ephemeralMasterKeyService;
        private readonly IReadOnlyList<string> _keyHeaders;

        /// <summary>
        /// Initializes a new instance of the <see cref="MasterKeyAuthenticationHandler"/> class.
        /// </summary>
        /// <param name="options">The monitor for authentication scheme options</param>
        /// <param name="logger">The logger factory</param>
        /// <param name="encoder">The URL encoder</param>
        /// <param name="configuration">The application configuration</param>
        /// <param name="ephemeralMasterKeyService">The ephemeral master key service</param>
        /// <param name="securityOptions">Configured Admin authentication header names</param>
        public MasterKeyAuthenticationHandler(
            IOptionsMonitor<MasterKeyAuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration configuration,
            IEphemeralMasterKeyService ephemeralMasterKeyService,
            IOptions<AdminSecurityOptions> securityOptions)
            : base(options, logger, encoder)
        {
            // Get backend auth key from environment variable first, then fallback to configuration
            _masterKey = Environment.GetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY") 
                ?? configuration["AdminApi:MasterKey"];
            _ephemeralMasterKeyService = ephemeralMasterKeyService ?? throw new ArgumentNullException(nameof(ephemeralMasterKeyService));
            ArgumentNullException.ThrowIfNull(securityOptions);
            _keyHeaders = MasterKeyCredentialReader.GetKeyHeaders(securityOptions.Value.ApiAuth);
        }

        /// <summary>
        /// Handles the authentication by validating the master key from request headers.
        /// </summary>
        /// <returns>The result of the authentication attempt</returns>
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sw = Stopwatch.StartNew();

            // Allow health check endpoints without authentication
            if (Context.Request.Path.StartsWithSegments("/health/live") || 
                Context.Request.Path.StartsWithSegments("/health/ready") ||
                Context.Request.Path.Value == "/health")
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, "HealthCheck"),
                    new Claim(ClaimTypes.NameIdentifier, "health-check")
                };

                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);

                AdminAuthMetrics.RecordSuccess("HealthCheck");
                return AuthenticateResult.Success(ticket);
            }

            var providedKey = MasterKeyCredentialReader.Read(Context.Request, _keyHeaders);

            if (string.IsNullOrEmpty(providedKey))
            {
                Logger.LogWarning("Authentication failed: no API key provided for {Path}",
                    LoggingSanitizer.S(Context.Request.Path.ToString()));
                sw.Stop();
                AdminAuthMetrics.RecordFailure("MasterKey", "missing_key");
                AdminAuthMetrics.RecordDuration("MasterKey", sw.Elapsed.TotalSeconds);
                return AuthenticateResult.Fail("Missing master key");
            }

            // Check if this is an ephemeral master key
            if (providedKey.StartsWith("emk_", StringComparison.Ordinal))
            {
                // Validate and mark as consumed; cleanup middleware deletes it after the request.
                var isValid = await _ephemeralMasterKeyService.ValidateAndConsumeKeyAsync(providedKey);

                if (!isValid)
                {
                    var keyExists = await _ephemeralMasterKeyService.KeyExistsAsync(providedKey);
                    if (!keyExists)
                    {
                        Logger.LogWarning("Ephemeral master key not found: {Key}", SanitizeKeyForLogging(providedKey));
                        sw.Stop();
                        AdminAuthMetrics.RecordFailure("EphemeralKey", "not_found");
                        AdminAuthMetrics.RecordDuration("EphemeralKey", sw.Elapsed.TotalSeconds);
                        return AuthenticateResult.Fail("Ephemeral master key not found");
                    }

                    Logger.LogWarning("Ephemeral master key validation failed: {Key}", SanitizeKeyForLogging(providedKey));
                    sw.Stop();
                    AdminAuthMetrics.RecordFailure("EphemeralKey", "expired");
                    AdminAuthMetrics.RecordDuration("EphemeralKey", sw.Elapsed.TotalSeconds);
                    return AuthenticateResult.Fail("Ephemeral master key expired");
                }

                Context.Items["EphemeralMasterKey"] = providedKey;
                Context.Items["DeleteEphemeralMasterKey"] = true;

                Logger.LogInformation("Authenticated via ephemeral master key");

                // Create authenticated user - same as regular master key auth for transparency
                var emkClaims = new[]
                {
                    new Claim(ClaimTypes.Name, "AdminUser"),
                    new Claim(ClaimTypes.NameIdentifier, "admin"),
                    new Claim("MasterKey", "true")
                };

                var emkIdentity = new ClaimsIdentity(emkClaims, Scheme.Name);
                var emkPrincipal = new ClaimsPrincipal(emkIdentity);
                var emkTicket = new AuthenticationTicket(emkPrincipal, Scheme.Name);

                sw.Stop();
                AdminAuthMetrics.RecordSuccess("EphemeralKey");
                AdminAuthMetrics.RecordDuration("EphemeralKey", sw.Elapsed.TotalSeconds);
                return AuthenticateResult.Success(emkTicket);
            }

            // Regular master key validation
            if (string.IsNullOrEmpty(_masterKey))
            {
                Logger.LogError("Backend auth key is not configured. Set CONDUIT_API_TO_API_BACKEND_AUTH_KEY environment variable.");
                sw.Stop();
                AdminAuthMetrics.RecordFailure("MasterKey", "not_configured");
                AdminAuthMetrics.RecordDuration("MasterKey", sw.Elapsed.TotalSeconds);
                return AuthenticateResult.Fail("Master key not configured");
            }

            if (!ConstantTimeComparer.Equals(providedKey, _masterKey))
            {
                Logger.LogWarning("Authentication failed: invalid master key provided for {Path} from {ClientIp}",
                    LoggingSanitizer.S(Context.Request.Path.ToString()),
                    Context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                sw.Stop();
                AdminAuthMetrics.RecordFailure("MasterKey", "invalid_key");
                AdminAuthMetrics.RecordDuration("MasterKey", sw.Elapsed.TotalSeconds);
                return AuthenticateResult.Fail("Invalid master key");
            }

            // Create authenticated user
            var authClaims = new[]
            {
                new Claim(ClaimTypes.Name, "AdminUser"),
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim("MasterKey", "true")
            };

            var authIdentity = new ClaimsIdentity(authClaims, Scheme.Name);
            var authPrincipal = new ClaimsPrincipal(authIdentity);
            var authTicket = new AuthenticationTicket(authPrincipal, Scheme.Name);

            sw.Stop();
            AdminAuthMetrics.RecordSuccess("MasterKey");
            AdminAuthMetrics.RecordDuration("MasterKey", sw.Elapsed.TotalSeconds);
            return AuthenticateResult.Success(authTicket);
        }

        private static string SanitizeKeyForLogging(string key)
        {
            // Only show first 10 characters of the key for security
            return SpanHelper.MaskSecret(key);
        }
    }

    /// <summary>
    /// Options for the master key authentication scheme
    /// </summary>
    public class MasterKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions
    {
    }
}
