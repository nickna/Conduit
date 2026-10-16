using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using ConduitLLM.Core.Utilities;
using ConduitLLM.Security.Authorization;

namespace ConduitLLM.Security.Middleware;

/// <summary>
/// Middleware that protects health endpoints by requiring either:
/// <list type="bullet">
/// <item>The request originates from a private network (10.x, 172.16-31.x, 192.168.x, 127.x)</item>
/// <item>A valid health monitoring key is provided via the X-Conduit-Health-Key header</item>
/// </list>
/// </summary>
/// <remarks>
/// This middleware returns 404 Not Found for unauthorized external requests to hide
/// the existence of health endpoints from potential attackers (security through obscurity).
/// </remarks>
public class HealthEndpointAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _healthKey;
    private readonly ILogger<HealthEndpointAuthorizationMiddleware> _logger;

    /// <summary>
    /// Route segment roots that are considered health endpoints.
    /// </summary>
    private static readonly string[] HealthPathPrefixes = new[]
    {
        "/health",
        "/api/health",
        "/v1/admin/health-status"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthEndpointAuthorizationMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">The logger instance.</param>
    public HealthEndpointAuthorizationMiddleware(
        RequestDelegate next,
        ILogger<HealthEndpointAuthorizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _healthKey = Environment.GetEnvironmentVariable(HealthKeyAuthorizationHandler.HealthKeyEnvVar);
    }

    /// <summary>
    /// Processes the HTTP request and enforces health endpoint authorization.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthEndpoint(context.Request.Path))
        {
            if (!IsAuthorized(context))
            {
                _logger.LogDebug(
                    "Health endpoint access denied for {Path} from {RemoteIp}: returning 404",
                    context.Request.Path,
                    context.Connection.RemoteIpAddress);

                // Return 404 to hide endpoint existence from unauthorized external requests
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            _logger.LogDebug(
                "Health endpoint access granted for {Path} from {RemoteIp}",
                context.Request.Path,
                context.Connection.RemoteIpAddress);
        }

        await _next(context);
    }

    /// <summary>
    /// Determines if the request path is a health endpoint.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>True if the path is a health endpoint, false otherwise.</returns>
    private static bool IsHealthEndpoint(PathString path)
    {
        if (!path.HasValue)
            return false;

        foreach (var prefix in HealthPathPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if the request is authorized to access health endpoints.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>True if authorized, false otherwise.</returns>
    private bool IsAuthorized(HttpContext context)
    {
        // Private network requests are always authorized
        if (IpAddressHelper.IsPrivateNetworkRequest(context))
        {
            return true;
        }

        // External requests: check for valid health key header
        if (!string.IsNullOrEmpty(_healthKey) &&
            context.Request.Headers.TryGetValue(HealthKeyAuthorizationHandler.HealthKeyHeaderName, out var providedKey) &&
            !string.IsNullOrEmpty(providedKey) &&
            string.Equals(providedKey, _healthKey, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}

/// <summary>
/// Extension methods for adding health endpoint authorization middleware.
/// </summary>
public static class HealthEndpointAuthorizationMiddlewareExtensions
{
    /// <summary>
    /// Adds the health endpoint authorization middleware to the application pipeline.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    /// <remarks>
    /// This middleware should be added early in the pipeline, before authentication,
    /// to ensure health endpoints are protected even before other middleware runs.
    /// </remarks>
    public static IApplicationBuilder UseHealthEndpointAuthorization(this IApplicationBuilder app)
    {
        return app.UseMiddleware<HealthEndpointAuthorizationMiddleware>();
    }
}
