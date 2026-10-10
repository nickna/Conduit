using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;
using ConduitLLM.Security.Services;
using ConduitLLM.Security.Cryptography;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Security;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Security service implementation for Admin API.
    /// Handles master key authentication, IP banning, rate limiting, and IP filtering.
    /// </summary>
    public class SecurityService : SecurityServiceBase, IAdminSecurityService
    {
        private readonly AdminSecurityOptions _options;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory? _serviceScopeFactory;
        private readonly IReadOnlyList<string> _keyHeaders;

        /// <inheritdoc/>
        protected override string ServiceName => "admin-api";

        /// <inheritdoc/>
        protected override SecurityOptionsBase Options => _options;

        /// <summary>
        /// Initializes a new instance of the Admin SecurityService
        /// </summary>
        public SecurityService(
            IOptions<AdminSecurityOptions> options,
            IConfiguration configuration,
            ILogger<SecurityService> logger,
            IMemoryCache memoryCache,
            IDistributedCache? distributedCache = null,
            IServiceScopeFactory? serviceScopeFactory = null)
            : base(logger, memoryCache, distributedCache)
        {
            _options = options.Value;
            _configuration = configuration;
            _serviceScopeFactory = serviceScopeFactory;
            _keyHeaders = MasterKeyCredentialReader.GetKeyHeaders(_options.ApiAuth);
        }

        /// <inheritdoc/>
        public override async Task<SecurityCheckResult> IsRequestAllowedAsync(HttpContext context)
        {
            var clientIp = GetClientIpAddress(context);
            var path = context.Request.Path.Value ?? "";

            // Check API key authentication (unless excluded path)
            if (!IsPathExcluded(path, new List<string> { "/health", "/swagger", "/scalar", "/openapi" }))
            {
                if (!await IsApiKeyValidAsync(context))
                {
                    await RecordFailedAuthAsync(clientIp);

                    Logger.LogWarning(
                        "Authentication failed for {Method} {Path} from {ClientIp}",
                        context.Request.Method, path, clientIp);

                    return SecurityCheckResult.Denied("Invalid or missing API key", 401);
                }
            }

            // Check if IP is banned
            if (await IsIpBannedAsync(clientIp))
            {
                Logger.LogWarning(
                    "Banned IP {ClientIp} attempted access to {Method} {Path}",
                    clientIp, context.Request.Method, path);

                return SecurityCheckResult.Denied("IP is banned due to excessive failed authentication attempts");
            }

            // Check rate limiting
            if (_options.RateLimiting.Enabled && !IsPathExcluded(path, _options.RateLimiting.ExcludedPaths))
            {
                var rateLimitResult = await CheckIpRateLimitAsync(clientIp);
                if (!rateLimitResult.IsAllowed)
                {
                    return rateLimitResult;
                }
            }

            // Check IP filtering
            if (_options.IpFiltering.Enabled && !IsPathExcluded(path, _options.IpFiltering.ExcludedPaths))
            {
                var ipFilterResult = await CheckIpFilterAsync(clientIp);
                if (!ipFilterResult.IsAllowed)
                {
                    return ipFilterResult;
                }
            }

            Logger.LogDebug(
                "Request authorized: {Method} {Path} from {ClientIp}",
                context.Request.Method, path, clientIp);

            return SecurityCheckResult.Allowed();
        }

        /// <inheritdoc/>
        public bool ValidateApiKey(string providedKey)
        {
            var masterKey = Environment.GetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY")
                           ?? _configuration["AdminApi:MasterKey"];

            return !string.IsNullOrEmpty(masterKey) &&
                   ConstantTimeComparer.Equals(providedKey, masterKey);
        }

        /// <inheritdoc/>
        protected override async Task<SecurityCheckResult> CheckDatabaseIpFilterAsync(string ipAddress)
        {
            if (_serviceScopeFactory == null)
                return SecurityCheckResult.Allowed();

            using var scope = _serviceScopeFactory.CreateScope();
            var ipFilterService = scope.ServiceProvider.GetRequiredService<IAdminIpFilterService>();
            var isAllowedByDb = await ipFilterService.IsIpAllowedAsync(ipAddress);

            if (!isAllowedByDb)
            {
                Logger.LogWarning("IP {IpAddress} blocked by database IP filter", ipAddress);
                return SecurityCheckResult.Denied("IP address not allowed");
            }

            return SecurityCheckResult.Allowed();
        }

        private async Task<bool> IsApiKeyValidAsync(HttpContext context)
        {
            var providedKey = MasterKeyCredentialReader.Read(context.Request, _keyHeaders);
            return !string.IsNullOrEmpty(providedKey) && await IsProvidedKeyValidAsync(providedKey);
        }

        private async Task<bool> IsProvidedKeyValidAsync(string providedKey)
        {
            if (!providedKey.StartsWith("emk_", StringComparison.Ordinal))
            {
                return ValidateApiKey(providedKey);
            }

            if (_serviceScopeFactory is null)
            {
                return false;
            }

            using var scope = _serviceScopeFactory.CreateScope();
            var ephemeralKeyService = scope.ServiceProvider.GetRequiredService<IEphemeralMasterKeyService>();
            return await ephemeralKeyService.IsKeyValidAsync(providedKey);
        }
    }
}
