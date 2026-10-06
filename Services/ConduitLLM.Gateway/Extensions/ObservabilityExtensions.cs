using ConduitLLM.Configuration.Interceptors;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering observability services (opt-in tracing, query monitoring)
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Adds opt-in OpenTelemetry tracing and query monitoring. Metrics are served by
    /// prometheus-net; see the remarks in the method body.
    /// </summary>
    public static IServiceCollection AddObservabilityServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Metrics are exported by prometheus-net's meter adapter, which subscribes to every
        // System.Diagnostics.Metrics instrument and serves /metrics. There is deliberately no
        // OpenTelemetry MeterProvider: a second one would duplicate aggregation state for a
        // pipeline with no exporter attached.
        //
        // Tracing is opt-in and requires an explicitly configured collector. Defaulting it on
        // pointed every deployment at a localhost collector that does not exist.
        var otlpEndpoint = configuration["Telemetry:OtlpEndpoint"];
        var tracingEnabled = configuration.GetValue<bool>("Telemetry:TracingEnabled", false)
            && !string.IsNullOrWhiteSpace(otlpEndpoint);

        if (tracingEnabled)
        {
            services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(serviceName: "ConduitLLM.Gateway", serviceVersion: "1.0.0"))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Filter out health check endpoints to reduce noise
                        options.Filter = httpContext =>
                            !httpContext.Request.Path.StartsWithSegments("/health") &&
                            !httpContext.Request.Path.StartsWithSegments("/metrics");
                    })
                    .AddHttpClientInstrumentation()
#if !CONDUIT_NATIVE_AOT
                    .AddSqlClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
#endif
                    .AddRedisInstrumentation()
                    .AddSource("ConduitLLM.SignalR")
                    .AddSource("ConduitLLM.MediaGeneration")
                    .AddSource("ConduitLLM.Gateway.Requests")
                    .AddSource("ConduitLLM.Providers")
                    // Wolverine message-processing spans (#931).
                    .AddSource("Wolverine")
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint!);
                    });
            });
        }

        // Configure query monitoring for performance tracking
        services.Configure<QueryMonitoringOptions>(
            configuration.GetSection(QueryMonitoringOptions.SectionName));
        services.AddSingleton<QueryMonitoringInterceptor>();

        // Register task processing metrics (per-instance)
        services.AddHostedService<Services.TaskProcessingMetricsService>();

        // Note: BusinessMetricsService and GatewayOperationsMetricsService are registered
        // in Program.Monitoring.cs with leader election to avoid duplicate metrics in scaled deployments

        return services;
    }
}
