using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Options;
using Prometheus;

namespace ConduitLLM.Gateway.Authentication
{
    /// <summary>
    /// Authentication handler for Virtual Key authentication in the Gateway API
    /// </summary>
    public class VirtualKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IVirtualKeyRuntimeService _virtualKeyService;
        private readonly IEphemeralKeyService _ephemeralKeyService;
        private readonly IReadOnlyList<string> _keyHeaders;

        /// <summary>
        /// Initializes a new instance of the VirtualKeyAuthenticationHandler
        /// </summary>
        public VirtualKeyAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IVirtualKeyRuntimeService virtualKeyService,
            IEphemeralKeyService ephemeralKeyService,
            IOptions<GatewaySecurityOptions> securityOptions)
            : base(options, logger, encoder)
        {
            _virtualKeyService = virtualKeyService;
            _ephemeralKeyService = ephemeralKeyService;
            ArgumentNullException.ThrowIfNull(securityOptions);
            _keyHeaders = securityOptions.Value.VirtualKey.KeyHeaders;
        }

        /// <summary>
        /// Handles the authentication for the request
        /// </summary>
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            using var activity = GatewayRequestMetrics.StartAuthenticationActivity("VirtualKey");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Skip authentication for excluded paths
                if (IsPathExcluded(Context.Request.Path))
                {
                    // Create an anonymous identity for excluded paths
                    var anonymousIdentity = new ClaimsIdentity();
                    var anonymousPrincipal = new ClaimsPrincipal(anonymousIdentity);
                    var anonymousTicket = new AuthenticationTicket(anonymousPrincipal, Scheme.Name);
                    return AuthenticateResult.Success(anonymousTicket);
                }

                // Extract Virtual Key from request
                var providedKey = ExtractVirtualKey(Context);

                if (string.IsNullOrEmpty(providedKey))
                {
                    // Return NoResult to allow other authentication schemes to be tried
                    // Only log at Debug level since this is expected when using other auth schemes
                    Logger.LogDebug("No Virtual Key found in request to {Path} from IP {IP}",
                        Context.Request.Path, GetClientIpAddress(Context));
                    GatewayAuthMetrics.RecordNoResult("VirtualKey");
                    return AuthenticateResult.NoResult();
                }

                // Check if this is an ephemeral key (starts with "ek_")
                string? virtualKey;
                bool isEphemeralKey = false;

                if (providedKey.StartsWith("ek_", StringComparison.Ordinal))
                {
                    isEphemeralKey = true;
                    Logger.LogDebug("Processing ephemeral key authentication");

                    // Get the virtual key data without consuming
                    var keyData = await _ephemeralKeyService.GetKeyDataAsync(providedKey);
                    if (keyData == null)
                    {
                        Logger.LogWarning("Ephemeral key not found: {Key}", SanitizeKeyForLogging(providedKey));
                        GatewayAuthMetrics.RecordFailure("VirtualKey", "ephemeral_not_found");
                        return AuthenticateResult.Fail("Invalid ephemeral key");
                    }

                    // Check if expired
                    if (keyData.ExpiresAt < DateTimeOffset.UtcNow)
                    {
                        Logger.LogWarning("Ephemeral key expired: {Key}", SanitizeKeyForLogging(providedKey));
                        GatewayAuthMetrics.RecordFailure("VirtualKey", "ephemeral_expired");
                        return AuthenticateResult.Fail("Ephemeral key expired");
                    }

                    // Get the actual virtual key
                    virtualKey = await _ephemeralKeyService.GetVirtualKeyAsync(providedKey);
                    if (string.IsNullOrEmpty(virtualKey))
                    {
                        Logger.LogWarning("Could not retrieve virtual key from ephemeral key: {Key}", SanitizeKeyForLogging(providedKey));
                        GatewayAuthMetrics.RecordFailure("VirtualKey", "ephemeral_invalid");
                        return AuthenticateResult.Fail("Invalid ephemeral key");
                    }

                    Logger.LogInformation("Ephemeral key validated, using virtual key ID {VirtualKeyId}", keyData.VirtualKeyId);
                }
                else
                {
                    // Regular virtual key
                    virtualKey = providedKey;
                }

                // Validate the Virtual Key for authentication only (no balance check)
                // virtualKey is guaranteed to be non-null at this point due to earlier validation
                var validation = await _virtualKeyService.ValidateVirtualKeyForAuthenticationAsync(virtualKey!);
                if (!validation.IsValid || validation.Key is null)
                {
                    Logger.LogWarning("Virtual Key validation failed with {FailureCode} in request to {Path} from IP {IP}",
                        validation.FailureCode ?? "unknown",
                        Context.Request.Path,
                        GetClientIpAddress(Context));
                    GatewayAuthMetrics.RecordFailure("VirtualKey", validation.FailureCode ?? "invalid_key");
                    return AuthenticateResult.Fail("Invalid Virtual Key");
                }

                var keyEntity = validation.Key;

                // Create claims for the authenticated user
                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, keyEntity.KeyName ?? "Unknown"),
                    new Claim("VirtualKeyId", keyEntity.Id.ToString()),
                    new Claim("VirtualKey", virtualKey)
                };

                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);

                // Store virtual key info in HttpContext for usage tracking
                Context.Items["VirtualKeyId"] = keyEntity.Id;
                Context.Items["VirtualKey"] = virtualKey;
                Context.Items["VirtualKeyEntity"] = keyEntity;
                Context.Items["RequestStartTime"] = DateTime.UtcNow;

                // Store rate-limit config so VirtualKeyRateLimitMiddleware can enforce limits
                // without re-fetching the key. KeyHash is the partition key for rate limiting;
                // null RPM/RPD means "unlimited" for this key.
                Context.Items["VirtualKey.KeyHash"] = keyEntity.KeyHash;
                Context.Items["VirtualKey.RateLimitRpm"] = keyEntity.RateLimitRpm;
                Context.Items["VirtualKey.RateLimitRpd"] = keyEntity.RateLimitRpd;
                Context.Items["VirtualKey.RateLimitTpm"] = keyEntity.RateLimitTpm;
                Context.Items["VirtualKey.MaxParallelRequests"] = keyEntity.MaxParallelRequests;
                Context.Items["VirtualKey.RateLimitPriority"] = keyEntity.RateLimitPriority;
                Context.Items["VirtualKey.ModelRateLimits"] = keyEntity.ModelRateLimits;

                // Group ceilings apply in addition to the key's own, so they travel with the
                // request rather than being looked up again downstream.
                var group = keyEntity.VirtualKeyGroup;
                if (group is not null)
                {
                    Context.Items["VirtualKey.GroupId"] = group.Id;
                    Context.Items["VirtualKeyGroup.RateLimitRpm"] = group.RateLimitRpm;
                    Context.Items["VirtualKeyGroup.RateLimitRpd"] = group.RateLimitRpd;
                    Context.Items["VirtualKeyGroup.RateLimitTpm"] = group.RateLimitTpm;
                    Context.Items["VirtualKeyGroup.MaxParallelRequests"] = group.MaxParallelRequests;
                }

                // Store ephemeral key status for logging/auditing
                if (isEphemeralKey)
                {
                    Context.Items["IsEphemeralKey"] = true;
                    Context.Items["AuthType"] = "EphemeralKey";
                }
                else
                {
                    Context.Items["AuthType"] = "VirtualKey";
                }

                activity?.SetTag("gateway.virtual_key_id", keyEntity.Id);
                activity?.SetTag("gateway.auth_result", "success");
                Logger.LogDebug("Successfully authenticated Virtual Key {KeyName} for {Path}",
                    LoggingSanitizer.S(keyEntity.KeyName), LoggingSanitizer.S(Context.Request.Path.ToString()));

                GatewayAuthMetrics.RecordSuccess("VirtualKey");
                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("gateway.auth_result", "error");
                Logger.LogError(ex, "Error during Virtual Key authentication for {Path}", LoggingSanitizer.S(Context.Request.Path.ToString()));
                GatewayAuthMetrics.RecordError("VirtualKey");
                return AuthenticateResult.Fail("Authentication error");
            }
            finally
            {
                stopwatch.Stop();
                GatewayAuthMetrics.RecordDuration("VirtualKey", stopwatch.Elapsed.TotalSeconds);
            }
        }

        /// <summary>
        /// Determines if the path should be excluded from authentication
        /// </summary>
        private bool IsPathExcluded(string path)
        {
            var excludedPaths = new[]
            {
                "/health",
                "/health/ready",
                "/health/live",
                "/v1/conduit/media/public"
            };

            return Array.Exists(excludedPaths, excludedPath => 
                path.StartsWith(excludedPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Extracts the Virtual Key from the request
        /// </summary>
        private string? ExtractVirtualKey(Microsoft.AspNetCore.Http.HttpContext context) =>
            VirtualKeyExtractor.Extract(context, _keyHeaders);

        /// <summary>
        /// Sanitizes a key for logging by showing only first few characters
        /// </summary>
        private static string SanitizeKeyForLogging(string key)
        {
            return SpanHelper.MaskSecret(key);
        }

        /// <summary>
        /// Gets the client IP address from the request
        /// </summary>
        private string GetClientIpAddress(Microsoft.AspNetCore.Http.HttpContext context)
        {
            // Client IP comes from the trusted-proxy-vetted connection (ForwardedHeadersMiddleware).
            // Forwarded headers are NOT read here — they are client-controlled and spoofable.
            return IpAddressHelper.GetClientIpAddress(context);
        }
    }
}
