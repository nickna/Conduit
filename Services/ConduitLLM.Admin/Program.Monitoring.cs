using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Middleware;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Core.Extensions;

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Prometheus;

namespace ConduitLLM.Admin;

public partial class Program
{
    /// <summary>
    /// Configures health checks, OpenTelemetry metrics/tracing, and monitoring services.
    /// </summary>
    private static void ConfigureMonitoringServices(WebApplicationBuilder builder, ILogger startupLogger)
    {
        // Add basic health checks
        var healthChecksBuilder = builder.Services.AddHealthChecks();

        // Gate /health/ready on the schema being current. Tag must be "ready" —
        // that's what the readiness endpoint filters on. Wait mode holds readiness
        // down until the explicit migrator catches the schema up; Skip bypasses it.
        healthChecksBuilder.AddCheck<ConduitLLM.Configuration.HealthChecks.PendingMigrationsReadinessCheck>(
            "pending_migrations",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            tags: new[] { "ready", "database", "migrations" });

        // Wolverine bus health check (#931): probes the Postgres message store
        // (inbox/outbox/scheduled/dead-letter counts). Only on the Postgresql
        // transport — the in-memory dev/CI mode has no store to probe.
        if (ConduitLLM.Configuration.Messaging.MessagingBackendResolver.Resolve(builder.Configuration)
                == ConduitLLM.Configuration.Messaging.MessagingBackend.Wolverine
            && !ConduitLLM.Configuration.Messaging.Wolverine.WolverineMessagingExtensions
                .UsesInMemoryTransport(builder.Configuration))
        {
            var deadLetterThreshold = builder.Configuration.GetValue(
                ConduitLLM.Configuration.Messaging.Wolverine.WolverineBusHealthCheck.DeadLetterThresholdKey, 1);

            healthChecksBuilder.AddTypeActivatedCheck<ConduitLLM.Configuration.Messaging.Wolverine.WolverineBusHealthCheck>(
                "wolverine_bus",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: new[] { "messaging", "wolverine", "ready" },
                args: new object[] { deadLetterThreshold });
        }

        // Add connection pool warmer with coordinated warming to prevent thundering herd during deployments
        builder.Services.AddCoordinatedConnectionPoolWarming(builder.Configuration, "AdminAPI");

        // Metrics are exported by prometheus-net's meter adapter, which subscribes to every
        // System.Diagnostics.Metrics instrument and serves /metrics. There is deliberately no
        // OpenTelemetry MeterProvider: a second one would duplicate aggregation state for a
        // pipeline with no exporter attached.
        //
        // Tracing is opt-in and requires an explicitly configured collector. Defaulting it on
        // pointed every deployment at a localhost collector that does not exist.
        var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
        var tracingEnabled = builder.Configuration.GetValue<bool>("Telemetry:TracingEnabled", false)
            && !string.IsNullOrWhiteSpace(otlpEndpoint);

        if (tracingEnabled)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(serviceName: "ConduitLLM.Admin", serviceVersion: "1.0.0"))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Filter out health check endpoints to reduce noise
                        options.Filter = httpContext =>
                            !httpContext.Request.Path.StartsWithSegments("/health") &&
                            !httpContext.Request.Path.StartsWithSegments("/metrics");
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource("ConduitLLM.Admin.Requests")
                    .AddSource("ConduitLLM.Providers")
                    // Wolverine message-processing spans (#931).
                    .AddSource("Wolverine")
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint!);
                    });
            });
            startupLogger.LogInformation("OpenTelemetry tracing enabled — exporting to {OtlpEndpoint}", otlpEndpoint);
        }
        else
        {
            startupLogger.LogInformation(
                "OpenTelemetry tracing disabled (set Telemetry:TracingEnabled=true and Telemetry:OtlpEndpoint to enable)");
        }

        // Add monitoring services - with leader election
#if !CONDUIT_NATIVE_AOT
        builder.Services.AddLeaderElectedHostedService<ConduitLLM.Admin.Services.AdminOperationsMetricsService>("AdminOperationsMetricsService");
#endif
    }

    /// <summary>
    /// Maps health check endpoints and enables Admin HTTP metric collection.
    /// </summary>
    private static void MapMonitoringEndpoints(WebApplication app)
    {
        // Map health check endpoints
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live")
        });
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready") || check.Tags.Count == 0
        });

#if CONDUIT_NATIVE_AOT
        var runtimeCapabilities = new AdminRuntimeCapabilitiesResponse(
            "native-aot", [], "typed-npgsql-selected-runtime-slices",
            [
                "postgresql-wolverine-persistence",
                "master-key-and-bearer-fallback-authentication",
                "typed-global-settings-http",
                "typed-ip-filter-http-and-enforcement",
                "typed-global-settings-cache",
                "typed-billing-audit-host",
                "release-owned-schema-wait",
                "readiness-and-database-health",
                "liveness-metrics-opentelemetry"
            ],
            [
                "ef-backed-admin-management",
                "pricing-tools-and-audit",
                "model-cost-canary",
                "startup-pricing-configuration-audit",
                "media-cleanup-and-reconciliation",
                "media-storage-startup-guard",
                "openrouter-metadata-sync",
                "database-count-operations-metrics",
                "billing-audit-reads-and-retention"
            ]);
        if (NativeAdminRuntimeProfileMiddleware.ReadWebhookRecoveryEnabled(app.Configuration))
            runtimeCapabilities = runtimeCapabilities with
            {
                IncludedFeatures = [.. runtimeCapabilities.IncludedFeatures, "experimental-webhook-recovery"]
            };
#else
        var runtimeCapabilities = new AdminRuntimeCapabilitiesResponse(
            "jit", [], "ef-core-supported",
            [
                "postgresql-wolverine-persistence",
                "master-key-and-bearer-fallback-authentication",
                "global-settings-http",
                "ip-filter-http-and-enforcement",
                "global-settings-cache",
                "billing-audit-host",
                "release-owned-schema-wait",
                "readiness-and-database-health",
                "liveness-metrics-opentelemetry",
                "ef-backed-admin-management",
                "pricing-tools-and-audit",
                "model-cost-canary",
                "startup-pricing-configuration-audit",
                "media-cleanup-and-reconciliation",
                "media-storage-startup-guard",
                "openrouter-metadata-sync",
                "database-count-operations-metrics",
                "billing-audit-reads-and-retention"
            ], []);
#endif
        app.MapGet("/health/runtime-capabilities", () =>
                Results.Json(runtimeCapabilities, AdminHttpJsonContext.Default.AdminRuntimeCapabilitiesResponse))
            .ExcludeFromDescription();
        app.Logger.LogInformation(
            "Admin runtime mode {RuntimeMode}; persistence: {PersistenceStatus}; excluded features: {ExcludedFeatures}",
            runtimeCapabilities.RuntimeMode,
            runtimeCapabilities.PersistenceStatus,
            runtimeCapabilities.ExcludedFeatures.Length == 0
                ? "none"
                : string.Join(',', runtimeCapabilities.ExcludedFeatures));

        app.Logger.LogInformation("Health check endpoints registered: /health, /health/live, /health/ready");

        // For the prometheus-net library metrics
        app.UseHttpMetrics(options =>
        {
            options.ReduceStatusCodeCardinality();
            options.RequestDuration.Enabled = false;
            options.RequestCount.Enabled = false;
        });
    }
}
