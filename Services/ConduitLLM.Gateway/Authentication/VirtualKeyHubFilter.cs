using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Security.Options;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Authentication
{
    /// <summary>
    /// Hub filter that validates virtual keys for SignalR connections
    /// </summary>
    public class VirtualKeyHubFilter : IHubFilter
    {
        private readonly IVirtualKeyRuntimeService _virtualKeyService;
        private readonly ILogger<VirtualKeyHubFilter> _logger;
        private readonly IReadOnlyList<string> _keyHeaders;

        /// <summary>
        /// Initializes a new instance of VirtualKeyHubFilter
        /// </summary>
        public VirtualKeyHubFilter(
            IVirtualKeyRuntimeService virtualKeyService,
            ILogger<VirtualKeyHubFilter> logger,
            IOptions<GatewaySecurityOptions> securityOptions)
        {
            _virtualKeyService = virtualKeyService;
            _logger = logger;
            ArgumentNullException.ThrowIfNull(securityOptions);
            _keyHeaders = securityOptions.Value.VirtualKey.KeyHeaders;
        }

        /// <summary>
        /// Called when a new connection is established
        /// </summary>
        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next)
        {
            // The public video hub authenticates each subscription with a short-lived
            // ephemeral key. Applying connection-level virtual-key authentication here
            // made that intentionally public browser flow unreachable.
            if (invocationContext.Hub is Hubs.PublicVideoGenerationHub)
            {
                return await next(invocationContext);
            }

            var httpContext = invocationContext.Context.GetHttpContext();
            
            // Check if already authenticated
            if (invocationContext.Context.Items.ContainsKey("VirtualKeyId"))
            {
                return await next(invocationContext);
            }

            // Extract and validate virtual key
            var virtualKey = ExtractVirtualKey(httpContext);
            
            if (string.IsNullOrEmpty(virtualKey))
            {
                _logger.LogWarning("Missing Virtual Key in SignalR method invocation: {Method}",
                    invocationContext.HubMethodName);
                throw new HubException("Authentication required");
            }

            // Validate the Virtual Key
            var validation = await _virtualKeyService.ValidateVirtualKeyForAuthenticationAsync(virtualKey);
            if (!validation.IsValid || validation.Key is null)
            {
                _logger.LogWarning("Virtual Key validation failed with {FailureCode} in SignalR method invocation: {Method}",
                    validation.FailureCode ?? "unknown", invocationContext.HubMethodName);
                throw new HubException("Invalid authentication");
            }

            var keyEntity = validation.Key;

            // Store virtual key information in the connection context
            invocationContext.Context.Items["VirtualKeyId"] = keyEntity.Id;
            invocationContext.Context.Items["VirtualKeyHash"] = keyEntity.KeyHash;
            invocationContext.Context.Items["VirtualKeyName"] = keyEntity.KeyName ?? "Unknown";
            invocationContext.Context.Items["VirtualKey"] = virtualKey;
            invocationContext.Context.Items["VirtualKey.RateLimitRpm"] = keyEntity.RateLimitRpm;
            invocationContext.Context.Items["VirtualKey.RateLimitRpd"] = keyEntity.RateLimitRpd;

            _logger.LogDebug("Authenticated Virtual Key {KeyName} for method {Method}",
                LoggingSanitizer.S(keyEntity.KeyName), invocationContext.HubMethodName);

            return await next(invocationContext);
        }

        /// <summary>
        /// Called when a client connects
        /// </summary>
        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            if (context.Hub is Hubs.PublicVideoGenerationHub)
            {
                await next(context);
                return;
            }

            var httpContext = context.Context.GetHttpContext();
            var virtualKey = ExtractVirtualKey(httpContext);
            
            if (string.IsNullOrEmpty(virtualKey))
            {
                _logger.LogWarning("Missing Virtual Key in SignalR connection from IP {IP}",
                    GetClientIpAddress(httpContext));
                context.Context.Abort();
                return;
            }

            // Validate the Virtual Key
            var validation = await _virtualKeyService.ValidateVirtualKeyForAuthenticationAsync(virtualKey);
            if (!validation.IsValid || validation.Key is null)
            {
                _logger.LogWarning("Virtual Key validation failed with {FailureCode} in SignalR connection from IP {IP}",
                    validation.FailureCode ?? "unknown", GetClientIpAddress(httpContext));
                context.Context.Abort();
                return;
            }

            var keyEntity = validation.Key;

            // Store virtual key information in the connection context
            context.Context.Items["VirtualKeyId"] = keyEntity.Id;
            context.Context.Items["VirtualKeyHash"] = keyEntity.KeyHash;
            context.Context.Items["VirtualKeyName"] = keyEntity.KeyName ?? "Unknown";
            context.Context.Items["VirtualKey"] = virtualKey;
            context.Context.Items["VirtualKey.RateLimitRpm"] = keyEntity.RateLimitRpm;
            context.Context.Items["VirtualKey.RateLimitRpd"] = keyEntity.RateLimitRpd;

            // Store claims in context items instead of modifying User
            // (User is read-only in SignalR hub context)

            _logger.LogInformation("Virtual Key {KeyName} connected to SignalR hub {Hub}",
                LoggingSanitizer.S(keyEntity.KeyName), context.Hub.GetType().Name);

            await next(context);
        }

        /// <summary>
        /// Called when a client disconnects
        /// </summary>
        public async Task OnDisconnectedAsync(
            HubLifetimeContext context,
            Exception? exception,
            Func<HubLifetimeContext, Exception?, Task> next)
        {
            var keyName = context.Context.Items.TryGetValue("VirtualKeyName", out var name) 
                ? name?.ToString() 
                : "Unknown";

            _logger.LogInformation("Virtual Key {KeyName} disconnected from SignalR hub {Hub}",
                LoggingSanitizer.S(keyName), context.Hub.GetType().Name);

            await next(context, exception);
        }

        /// <summary>
        /// Extracts the Virtual Key from the request
        /// </summary>
        private string? ExtractVirtualKey(Microsoft.AspNetCore.Http.HttpContext? httpContext) =>
            VirtualKeyExtractor.Extract(httpContext, _keyHeaders);

        /// <summary>
        /// Gets the client IP address from the request
        /// </summary>
        private string GetClientIpAddress(Microsoft.AspNetCore.Http.HttpContext? httpContext)
        {
            if (httpContext == null) return "unknown";

            // Client IP comes from the trusted-proxy-vetted connection (ForwardedHeadersMiddleware).
            // Forwarded headers are NOT read here — they are client-controlled and spoofable.
            return IpAddressHelper.GetClientIpAddress(httpContext);
        }
    }
}
