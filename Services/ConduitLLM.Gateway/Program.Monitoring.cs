using ConduitLLM.Configuration.Data;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Endpoints;

public partial class Program
{
    public static void ConfigureMonitoringServices(WebApplicationBuilder builder)
    {
        ConfigureOpenApiServices(builder);

        // Get Redis configuration for health checks
        var redisConnectionString = ConduitLLM.Configuration.Utilities.RedisUrlParser.ResolveConnectionString();

        var connectionStringManager = new ConduitLLM.Core.Data.ConnectionStringManager();
        var (dbProvider, dbConnectionString) = connectionStringManager.GetProviderAndConnectionString("CoreAPI");

        // Add standardized health checks (skip in test environment to avoid conflicts)
        if (builder.Environment.EnvironmentName != "Test")
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

            // Wolverine is the only messaging backend as of #932 (epic #909). Resolve still
            // runs so a stale rollback backend value fails the boot with a clear pointer to
            // Wolverine (see MessagingBackendResolver) instead of being silently ignored.
            _ = ConduitLLM.Configuration.Messaging.MessagingBackendResolver.Resolve(builder.Configuration);

            // Wolverine bus health check (#931): probes the Postgres message store
            // (inbox/outbox/scheduled/dead-letter counts). Only on the Postgresql
            // transport — the in-memory dev/CI mode has no store to probe.
            if (!ConduitLLM.Configuration.Messaging.Wolverine.WolverineMessagingExtensions
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

            // Add Redis health check if Redis is configured
            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                var redisConnStr = redisConnectionString; // Capture for closure
                healthChecksBuilder.AddTypeActivatedCheck<ConduitLLM.Configuration.HealthChecks.RedisHealthCheck>(
                    "redis",
                    failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                    tags: new[] { "cache", "redis", "billing" },
                    args: new object[] { redisConnStr });
                
                // Add circuit breaker health check
                healthChecksBuilder.AddCheck<ConduitLLM.Configuration.HealthChecks.RedisCircuitBreakerHealthCheck>(
                    "redis_circuit_breaker",
                    failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                    tags: new[] { "circuit_breaker", "redis", "resilience" });
                
                // Add leader election health check
                healthChecksBuilder.AddCheck<ConduitLLM.Gateway.HealthChecks.LeaderElectionHealthCheck>(
                    "leader_election",
                    failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                    tags: new[] { "leader_election", "background_services", "distributed" });
            }

        }

        // Add health monitoring services
        builder.Services.AddHealthMonitoring(builder.Configuration);

        // Add database migration services
        builder.Services.AddDatabaseMigration();

        // Customer-facing provider-error translation (CONDUIT_CUSTOMER_MODE)
        builder.Services.AddCustomerErrorTranslation();

        // Add connection pool warmer with coordinated warming to prevent thundering herd during deployments
        // Unlike leader election, ALL instances warm their pools, but in a staggered manner
        builder.Services.AddCoordinatedConnectionPoolWarming(builder.Configuration, "CoreAPI");

        // Add business metrics service for Prometheus/Grafana dashboards
        // Uses leader election to avoid duplicate metrics collection in scaled-out deployments
        builder.Services.AddLeaderElectedHostedService<ConduitLLM.Gateway.Services.BusinessMetricsService>(
            serviceProvider =>
            {
                var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
                var logger = serviceProvider.GetRequiredService<ILogger<ConduitLLM.Gateway.Services.BusinessMetricsService>>();
                return new ConduitLLM.Gateway.Services.BusinessMetricsService(scopeFactory, logger);
            },
            "BusinessMetricsService");

        // Add gateway operations metrics service for operation-level metrics
        // Tracks LLM, media, function, and routing operations.
        builder.Services.AddLeaderElectedHostedService<ConduitLLM.Gateway.Services.GatewayOperationsMetricsService>(
            serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<ConduitLLM.Gateway.Services.GatewayOperationsMetricsService>>();
                return new ConduitLLM.Gateway.Services.GatewayOperationsMetricsService(serviceProvider, logger);
            },
            "GatewayOperationsMetricsService");
    }

    /// <summary>Registers only the services required to describe HTTP endpoints.</summary>
    public static void ConfigureOpenApiServices(WebApplicationBuilder builder)
    {
        builder.Services.AddGatewayEndpointHandlers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer<ConduitLLM.Gateway.OpenApi.CoreApiDocumentTransformer>();
            options.AddOperationTransformer<ConduitLLM.Core.OpenApi.OperationMetadataTransformer>();
            options.AddOperationTransformer<ConduitLLM.Gateway.OpenApi.VirtualKeySecurityOperationTransformer>();
            options.AddOperationTransformer<ConduitLLM.Gateway.OpenApi.ResponseContractOperationTransformer>();
            options.AddSchemaTransformer<ConduitLLM.Gateway.OpenApi.StructuredJsonSchemaTransformer>();
            options.AddDocumentTransformer<ConduitLLM.Gateway.OpenApi.UnusedSchemaPruningDocumentTransformer>();
            options.AddDocumentTransformer<ConduitLLM.Core.OpenApi.OperationIdValidationDocumentTransformer>();
            options.AddDocumentTransformer<ConduitLLM.Core.OpenApi.DocumentationLineEndingDocumentTransformer>();
        });
    }
}
