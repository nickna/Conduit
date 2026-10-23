using System;
using System.Linq;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Middleware
{
    /// <summary>
    /// Middleware that blocks requests when Redis circuit breaker is open
    /// </summary>
    public class RedisAvailabilityMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RedisAvailabilityMiddleware> _logger;
        private readonly RedisCircuitBreakerOptions _options;

        public RedisAvailabilityMiddleware(
            RequestDelegate next,
            ILogger<RedisAvailabilityMiddleware> logger,
            IOptions<RedisCircuitBreakerOptions> options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task InvokeAsync(HttpContext context, IRedisCircuitBreaker circuitBreaker)
        {
            var path = context.Request.Path.Value ?? "";

            // Check if this path should bypass the circuit breaker
            if (ShouldBypass(path))
            {
                await _next(context);
                return;
            }

            // Check circuit breaker state
            if (circuitBreaker.IsOpen)
            {
                await HandleCircuitOpen(context, circuitBreaker);
                return;
            }

            // Allow request to proceed
            await _next(context);
        }

        private bool ShouldBypass(string path)
        {
            // Always bypass certain critical paths
            var criticalPaths = new[] { "/health", "/metrics", "/openapi", "/scalar" };
            if (criticalPaths.Any(cp => MatchesBypassPath(path, cp)))
            {
                return true;
            }

            // Check configured bypass paths
            if (_options.BypassPaths != null)
            {
                return _options.BypassPaths.Any(bp => MatchesBypassPath(path, bp));
            }

            return false;
        }

        private static bool MatchesBypassPath(string path, string bypassPath)
        {
            if (string.IsNullOrEmpty(bypassPath) ||
                !path.StartsWith(bypassPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A configured trailing slash already supplies the segment boundary.
            return path.Length == bypassPath.Length || bypassPath.EndsWith('/') ||
                path[bypassPath.Length] == '/';
        }

        private async Task HandleCircuitOpen(HttpContext context, IRedisCircuitBreaker circuitBreaker)
        {
            var stats = circuitBreaker.Statistics;
            
            _logger.LogWarning(
                "Request rejected due to open Redis circuit breaker. Path: {Path}, IP: {IP}",
                context.Request.Path,
                context.Connection.RemoteIpAddress);

            // Add Retry-After header if we know when circuit might close
            if (stats.TimeUntilHalfOpen != null && stats.TimeUntilHalfOpen.Value.TotalSeconds > 0)
            {
                context.Response.Headers.Append("Retry-After", 
                    Math.Ceiling(stats.TimeUntilHalfOpen.Value.TotalSeconds).ToString());
            }

            // Add custom headers for monitoring
            context.Response.Headers.Append("X-Circuit-Breaker-State", "Open");
            context.Response.Headers.Append("X-Service-Status", "Degraded");
            context.Response.Headers["x-request-id"] = context.TraceIdentifier;

            var metadata = _options.IncludeErrorDetails
                ? System.Text.Json.JsonSerializer.SerializeToElement(
                    new RedisCircuitBreakerMetadata(
                        stats.State.ToString(),
                        stats.TotalFailures,
                        stats.RejectedRequests,
                        stats.LastFailureAt?.ToString("O"),
                        stats.CircuitOpenedAt?.ToString("O"),
                        stats.TimeUntilHalfOpen?.TotalSeconds),
                    GatewayInternalJsonContext.Default.RedisCircuitBreakerMetadata)
                : (System.Text.Json.JsonElement?)null;

            await GatewayResults.OpenAIError(
                    StatusCodes.Status503ServiceUnavailable,
                    _options.OpenCircuitMessage,
                    "service_unavailable",
                    "server_error",
                    metadata: metadata)
                .ExecuteAsync(context);

            // Record metrics (will be implemented in metrics step)
            RecordRejectedRequest(context);
        }

        private void RecordRejectedRequest(HttpContext context)
        {
            // Record Prometheus metrics
            BillingMetrics.RecordCircuitBreakerRejection(
                context.Request.Path.Value ?? "unknown",
                context.Request.Method);
            
            _logger.LogInformation(
                "Redis circuit breaker rejected request. Method: {Method}, Path: {Path}, User: {User}",
                context.Request.Method,
                context.Request.Path,
                context.User?.Identity?.Name ?? "Anonymous");
        }
    }

    /// <summary>
    /// Extension methods for registering RedisAvailabilityMiddleware
    /// </summary>
    public static class RedisAvailabilityMiddlewareExtensions
    {
        /// <summary>
        /// Adds Redis availability checking middleware to the pipeline
        /// </summary>
        public static IApplicationBuilder UseRedisAvailability(this IApplicationBuilder app)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            return app.UseMiddleware<RedisAvailabilityMiddleware>();
        }
    }
}
