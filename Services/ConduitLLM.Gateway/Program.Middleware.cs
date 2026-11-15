using ConduitLLM.Configuration.Data;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Middleware;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Security.Extensions;
using ConduitLLM.Security.Middleware;
using Scalar.AspNetCore;

public partial class Program
{
    public static async Task ConfigureMiddleware(WebApplication app)
    {
        // Log deprecation warnings and validate Redis URL
        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            ConduitLLM.Configuration.Extensions.DeprecationWarnings.LogEnvironmentVariableDeprecations(logger);

            // Validate Redis URL if provided
            var envRedisUrl = Environment.GetEnvironmentVariable("REDIS_URL");
            if (!string.IsNullOrEmpty(envRedisUrl))
            {
                ConduitLLM.Configuration.Services.RedisUrlValidator.ValidateAndLog(envRedisUrl, logger, "Http Service");
            }
        }

        // Normal startup never mutates the database. MigrationWaitService gates
        // readiness until the explicit "migrate" release step has completed.
        // Resolve the real client IP via trusted proxies (must run before ANY middleware that reads
        // the client IP — correlation, auth, security). No-op unless CONDUIT_TRUSTED_PROXY_ENABLED=true.
        app.UseTrustedProxyForwardedHeaders();

        // Add correlation ID middleware (earliest — establishes correlation context for all downstream middleware)
        app.UseCorrelationId();

        // Add request tracking middleware (after correlation ID, wraps entire request lifecycle)
        app.UseGatewayRequestTracking();

        // Enable CORS
        app.UseCors();

        // Add health endpoint authorization (early in pipeline, before authentication)
        // This protects health endpoints from external access without valid key
        app.UseHealthEndpointAuthorization();

        // Enable Scalar API documentation in development
        if (app.Environment.IsDevelopment())
        {
            // Map the OpenAPI endpoint
            ConduitLLM.OpenApi.PublishedOpenApiExtensions.MapPublishedOpenApi(app, "/openapi/v1.json");

            // Map Scalar UI for interactive API documentation
            app.MapScalarApiReference();
        }

        // Add security headers
        app.UseGatewaySecurityHeaders();

        // Add Redis availability check middleware (must be early in pipeline)
        app.UseRedisAvailability();

        // Add authentication and authorization middleware
        app.UseAuthentication();
        // Private-network scrapes bypass authorization; external scrapes can authenticate.
        app.UseConduitPrometheusMetricsEndpoint();
        app.UseAuthorization();

        // Add ephemeral key cleanup middleware (must be after authentication)
        app.UseMiddleware<EphemeralKeyCleanupMiddleware>();

        // Note: VirtualKeyAuthenticationHandler is now used instead of middleware
        // The authentication handler is registered with the "VirtualKey" scheme above

        // Add OpenAI error handling middleware to map exceptions to proper HTTP status codes
        app.UseOpenAIErrorHandling();

        // Add usage tracking middleware to capture LLM usage from responses
        app.UseUsageTracking();

        // Add security middleware (IP filtering, ban checks)
        app.UseCoreApiSecurity();

        // Enforce per-virtual-key RPM/RPD rate limits (placed before metrics so rejected
        // requests aren't counted as served). Reads VirtualKey.KeyHash + RateLimitRpm/Rpd
        // stashed by VirtualKeyAuthenticationHandler; Backend-scheme requests pass through.
        app.UseVirtualKeyRateLimiting();

        // Add HTTP metrics middleware for comprehensive request tracking
        app.UseMiddleware<ConduitLLM.Gateway.Middleware.HttpMetricsMiddleware>();

        // Add timeout diagnostics middleware
        app.UseMiddleware<ConduitLLM.Core.Middleware.TimeoutDiagnosticsMiddleware>();

        // Enable WebSockets for real-time communication
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(120)
        });

        // Add controllers to the app
    }
}
