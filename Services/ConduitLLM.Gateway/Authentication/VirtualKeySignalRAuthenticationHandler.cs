using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Security.Options;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Authentication
{
    /// <summary>
    /// Custom authentication handler for SignalR hubs that validates virtual keys
    /// </summary>
    public class VirtualKeySignalRAuthenticationHandler : IAuthorizationHandler
    {
        private readonly IVirtualKeyRuntimeService _virtualKeyService;
        private readonly ILogger<VirtualKeySignalRAuthenticationHandler> _logger;
        private readonly IReadOnlyList<string> _keyHeaders;

        /// <summary>
        /// Initializes a new instance of VirtualKeySignalRAuthenticationHandler
        /// </summary>
        public VirtualKeySignalRAuthenticationHandler(
            IVirtualKeyRuntimeService virtualKeyService,
            ILogger<VirtualKeySignalRAuthenticationHandler> logger,
            IOptions<GatewaySecurityOptions> securityOptions)
        {
            _virtualKeyService = virtualKeyService;
            _logger = logger;
            ArgumentNullException.ThrowIfNull(securityOptions);
            _keyHeaders = securityOptions.Value.VirtualKey.KeyHeaders;
        }

        /// <summary>
        /// Handles authorization for SignalR hub connections and invocations
        /// </summary>
        public async Task HandleAsync(AuthorizationHandlerContext context)
        {
            if (context.Resource is HubInvocationContext hubContext)
            {
                // This is a hub method invocation
                var connectionContext = hubContext.Context;
                
                // Check if already authenticated during connection
                if (connectionContext.Items.ContainsKey("VirtualKeyId"))
                {
                    // Connection was already authenticated
                    context.Succeed(context.PendingRequirements.First());
                    return;
                }
            }
            else if (context.Resource is HttpContext httpContext)
            {
                // This is the initial connection attempt
                var virtualKey = ExtractVirtualKey(httpContext);
                
                if (string.IsNullOrEmpty(virtualKey))
                {
                    _logger.LogWarning("Missing Virtual Key in SignalR connection from IP {IP}",
                        GetClientIpAddress(httpContext));
                    context.Fail();
                    return;
                }

                // Validate the Virtual Key
                var validation = await _virtualKeyService.ValidateVirtualKeyForAuthenticationAsync(virtualKey);
                if (!validation.IsValid || validation.Key is null)
                {
                    _logger.LogWarning("Virtual Key validation failed with {FailureCode} in SignalR connection from IP {IP}",
                        validation.FailureCode ?? "unknown", GetClientIpAddress(httpContext));
                    context.Fail();
                    return;
                }

                var keyEntity = validation.Key;

                // Create claims for the authenticated connection
                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, keyEntity.KeyName ?? "Unknown"),
                    new Claim("VirtualKeyId", keyEntity.Id.ToString()),
                    new Claim("VirtualKey", virtualKey),
                    new Claim("VirtualKeyHash", keyEntity.KeyHash)
                };

                var identity = new ClaimsIdentity(claims, "VirtualKeySignalR");
                var principal = new ClaimsPrincipal(identity);
                
                // Replace the user context
                httpContext.User = principal;
                
                _logger.LogDebug("Successfully authenticated Virtual Key {KeyName} for SignalR connection",
                    keyEntity.KeyName);
                
                context.Succeed(context.PendingRequirements.First());
            }
            else
            {
                // Unknown resource type
                context.Fail();
            }
        }

        /// <summary>
        /// Extracts the Virtual Key from the request
        /// </summary>
        private string? ExtractVirtualKey(HttpContext context) =>
            VirtualKeyExtractor.Extract(context, _keyHeaders);

        /// <summary>
        /// Gets the client IP address from the request
        /// </summary>
        private string GetClientIpAddress(HttpContext context)
        {
            // Client IP comes from the trusted-proxy-vetted connection (ForwardedHeadersMiddleware).
            // Forwarded headers are NOT read here — they are client-controlled and spoofable.
            return IpAddressHelper.GetClientIpAddress(context);
        }
    }

    /// <summary>
    /// Authorization requirement for virtual key authentication
    /// </summary>
    public class VirtualKeySignalRAuthorizationRequirement : IAuthorizationRequirement
    {
    }
}
