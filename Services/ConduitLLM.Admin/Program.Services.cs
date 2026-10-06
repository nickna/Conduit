using ConduitLLM.Admin.Extensions;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;
using ConduitLLM.Providers.Extensions;

namespace ConduitLLM.Admin;

public partial class Program
{
    /// <summary>
    /// Configures core application services: DI registrations, Redis, SignalR, distributed cache.
    /// </summary>
    internal static void ConfigureCoreServices(WebApplicationBuilder builder, ILogger startupLogger)
    {
        // Add leader election service for distributed background service coordination
        builder.Services.AddLeaderElection();
        startupLogger.LogInformation("Leader election service configured for background service coordination");

        // Add Core services
        builder.Services.AddCoreServices(builder.Configuration, startupLogger);
        builder.Services.AddAsyncTaskServices();
        builder.Services.AddSingleton<IEventPublisher, EventPublisher>();

        // Add Configuration services
        builder.Services.AddConfigurationServices(builder.Configuration);

        // Add Provider services (needed for ILLMClientFactory)
        builder.Services.AddProviderServices();

        // Register named provider HttpClients so resilience policies attach to provider
        // traffic (key verification, model discovery) in the Admin API as well
        builder.Services.AddLLMProviderHttpClients();

        // Add Admin services
        builder.Services.AddAdminServices(builder.Configuration);

        // Configure Data Protection with Redis persistence
        var redisConnectionString = RedisUrlParser.ResolveConnectionString();
        builder.Services.AddConduitApplicationCache(builder.Configuration, builder.Environment.EnvironmentName, redisConnectionString);
        builder.Services.AddRedisDataProtection(redisConnectionString, "Conduit");

        // Add Redis as distributed cache for ephemeral key storage
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "conduit:";
            });
            startupLogger.LogInformation("Distributed cache configured with Redis");
        }
        else
        {
            // Fallback to in-memory cache if Redis is not configured
            builder.Services.AddDistributedMemoryCache();
            startupLogger.LogWarning("Using in-memory cache — ephemeral keys will not work across instances");
        }

        // Cross-service liveness heartbeat store (#1067): records the Gateway heartbeat the
        // Admin consumes over the event bus so the health dashboard reports the Gateway's real
        // status. Singleton so its in-process fallback (used when Redis is absent) is shared
        // between the event handler (writer) and the dashboard endpoint (reader).
        builder.Services.AddSingleton<ConduitLLM.Admin.Interfaces.IServiceHeartbeatStore, ConduitLLM.Admin.Services.ServiceHeartbeatStore>();
        builder.Services.AddHostedService<ConduitLLM.Admin.Services.AdminHeartbeatPublisher>();

        // Add media lifecycle services (scheduler, storage, distributed locking)
        builder.Services.AddMediaLifecycleServices(builder.Configuration);

        // Add OpenRouter metadata sync (drift detection + review + scheduled job)
        builder.Services.AddOpenRouterSyncServices(builder.Configuration);
    }
}
