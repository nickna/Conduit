using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Gateway.Serialization;

public partial class Program
{
    public static void ConfigureEndpoints(WebApplication app)
    {
        app.MapModelsEndpoints();
        app.MapGatewayApiEndpoints();

        var nativeRuntime = ConduitSignalRProtocolPolicy.IsNativeAot;
        var runtimeCapabilities = new RuntimeCapabilitiesResponse(
            nativeRuntime ? "native-aot" : "jit",
            ConduitSignalRProtocolPolicy.SupportedProtocols,
            nativeRuntime ? "compiled-model-query-precompilation-required" : "ef-core-supported",
            nativeRuntime
                ? [
                    "postgresql-wolverine-persistence",
                    "redis-signalr-backplane",
                    "redis-data-protection",
                    "json-signalr-negotiate-routing",
                    "unauthenticated-request-rejection",
                    "forwarded-headers",
                    "liveness-metrics-opentelemetry"
                ]
                : [
                    "postgresql",
                    "wolverine-postgresql",
                    "redis-cache",
                    "redis-rate-limits",
                    "redis-data-protection",
                    "redis-signalr-backplane",
                    "s3-compatible-media",
                    "provider-http",
                    "sse-streaming",
                    "bearer-and-api-key-authentication",
                    "forwarded-headers",
                    "health-metrics-opentelemetry"
                ],
            nativeRuntime
                ? [
                    "signalr-messagepack",
                    "ef-core-query-data-plane",
                    "authenticated-http-data-plane",
                    "authenticated-signalr-connections",
                    "redis-virtual-key-cache-and-rate-limits",
                    "provider-routing-and-streaming",
                    "s3-media-api-workflows",
                    "readiness-and-database-health"
                ]
                : []);

        app.Logger.LogInformation(
            "Runtime mode {RuntimeMode}; SignalR protocols: {Protocols}; excluded features: {ExcludedFeatures}",
            runtimeCapabilities.RuntimeMode,
            string.Join(',', runtimeCapabilities.SignalRProtocols),
            runtimeCapabilities.ExcludedFeatures.Length == 0
                ? "none"
                : string.Join(',', runtimeCapabilities.ExcludedFeatures));

        app.MapGet("/health/runtime-capabilities", () =>
                Results.Json(runtimeCapabilities, GatewayHttpJsonContext.Default.RuntimeCapabilitiesResponse))
            .ExcludeFromDescription();
        // Map SignalR hubs for real-time updates

        // Customer-facing hubs require virtual key authentication
        app.MapHub<ConduitLLM.Gateway.Hubs.VideoGenerationHub>("/hubs/video-generation")
            .RequireAuthorization();

        // Public video generation hub using task-scoped tokens (no virtual key required)
        app.MapHub<ConduitLLM.Gateway.Hubs.PublicVideoGenerationHub>("/hubs/public/video-generation");

        app.MapHub<ConduitLLM.Gateway.Hubs.ImageGenerationHub>("/hubs/image-generation")
            .RequireAuthorization();

        app.MapHub<ConduitLLM.Gateway.Hubs.TaskHub>("/hubs/tasks")
            .RequireAuthorization();

        app.MapHub<ConduitLLM.Gateway.Hubs.SystemNotificationHub>("/hubs/notifications")
            .RequireAuthorization();

        app.MapHub<ConduitLLM.Gateway.Hubs.SpendNotificationHub>("/hubs/spend")
            .RequireAuthorization();

        app.MapHub<ConduitLLM.Gateway.Hubs.WebhookDeliveryHub>("/hubs/webhooks")
            .RequireAuthorization();

        // Virtual key management hub for real-time key management updates
        app.MapHub<ConduitLLM.Gateway.Hubs.VirtualKeyManagementHub>("/hubs/virtual-key-management")
            .RequireAuthorization();

        // Map health check endpoints
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            // Exclude monitoring and performance checks from basic health endpoint
            Predicate = check => !check.Tags.Contains("monitoring") && !check.Tags.Contains("performance")
        });
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live")
        });
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready") || check.Tags.Count == 0
        });
    }
}
