using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Gateway.Services;
using StackExchange.Redis;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;

public partial class Program
{
    public static void ConfigureCachingServices(WebApplicationBuilder builder)
    {
        // Configure batch spending options
        builder.Services.Configure<BatchSpendingOptions>(
            builder.Configuration.GetSection(BatchSpendingOptions.SectionName));

        // Virtual Key service registration will be done after Redis configuration

        // Configure Redis connection for all Redis-dependent services
        var redisConnectionString = ConduitLLM.Configuration.Utilities.RedisUrlParser.ResolveConnectionString();
        builder.Services.AddConduitApplicationCache(builder.Configuration, builder.Environment.EnvironmentName, redisConnectionString);

        // Configure CacheOptions with the parsed Redis connection string for cache services.
        builder.Services.Configure<ConduitLLM.Configuration.Options.CacheOptions>(options =>
        {
            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                options.RedisConnectionString = redisConnectionString;
            }
        });

        builder.Services.AddRedisDataProtection(redisConnectionString, "Conduit");
        builder.Services.AddConduitDistributedLocks();

        // Configure Redis connection multiplexer FIRST (shared across all Redis services)
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            // Register Redis connection factory for proper connection pooling
            builder.Services.AddSingleton<ConduitLLM.Configuration.Services.RedisConnectionFactory>();

            // Use Redis-cached Virtual Key service for high-performance validation
            builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var factory = sp.GetRequiredService<ConduitLLM.Configuration.Services.RedisConnectionFactory>();
                var connectionTask = factory.GetConnectionAsync(redisConnectionString);
                var connection = connectionTask.GetAwaiter().GetResult();
                return connection;
            });

            // Add Redis distributed cache using the connection string directly
            // Note: This creates a separate connection pool from IConnectionMultiplexer
            // which is intentional for distributed cache operations
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "conduit-tasks:";
            });
        }
        else
        {
            // Fall back to in-memory distributed cache
            builder.Services.AddDistributedMemoryCache();
        }

        // Register Virtual Key service with optional Redis caching
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            // IConnectionMultiplexer and RedisConnectionFactory are already registered above

            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IVirtualKeyCache, RedisVirtualKeyCache>();

            // Register cache stampede prevention service (must be registered before caches that depend on it)
            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IDistributedCachePopulator, DistributedCachePopulator>();

            // Register additional Redis cache services
            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IProviderCache, RedisProviderCache>();
            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IModelCostCache, RedisModelCostCache>();
            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IProviderToolCache, RedisProviderToolCache>();

            // Register CachedApiVirtualKeyService with event publishing dependency
            builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IVirtualKeyService>(serviceProvider =>
            {
                var virtualKeyRepository = serviceProvider.GetRequiredService<IVirtualKeyRepository>();
                var spendHistoryRepository = serviceProvider.GetRequiredService<IVirtualKeySpendHistoryRepository>();
                var groupRepository = serviceProvider.GetRequiredService<IVirtualKeyGroupRepository>();
                var cache = serviceProvider.GetRequiredService<ConduitLLM.Core.Interfaces.IVirtualKeyCache>();
                var eventBus = serviceProvider.GetService<ConduitLLM.Configuration.Messaging.IEventBus>(); // Optional
                var logger = serviceProvider.GetRequiredService<ILogger<CachedApiVirtualKeyService>>();

                return new CachedApiVirtualKeyService(virtualKeyRepository, spendHistoryRepository, groupRepository, cache, eventBus, logger);
            });
        }
        else
        {
            // Fall back to direct database Virtual Key service
            builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IVirtualKeyService>(sp =>
            {
                var virtualKeyRepository = sp.GetRequiredService<IVirtualKeyRepository>();
                var groupRepository = sp.GetRequiredService<IVirtualKeyGroupRepository>();
                var spendHistoryRepository = sp.GetRequiredService<IVirtualKeySpendHistoryRepository>();
                var eventBus = sp.GetService<ConduitLLM.Configuration.Messaging.IEventBus>(); // Optional
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Gateway.Services.DirectApiVirtualKeyService>>();
                return new ConduitLLM.Gateway.Services.DirectApiVirtualKeyService(
                    virtualKeyRepository, groupRepository, spendHistoryRepository, eventBus, logger);
            });
        }

        // Register Webhook Delivery Tracker for deduplication and statistics
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            // Register the Redis tracker as the inner implementation
            builder.Services.AddSingleton<ConduitLLM.Core.Services.RedisWebhookDeliveryTracker>();

            // Add memory caching
            builder.Services.AddMemoryCache();

            // Register the cached wrapper as the main interface
            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IWebhookDeliveryTracker>(sp =>
            {
                var redisTracker = sp.GetRequiredService<ConduitLLM.Core.Services.RedisWebhookDeliveryTracker>();
                var memoryCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Core.Services.CachedWebhookDeliveryTracker>>();

                return new ConduitLLM.Core.Services.CachedWebhookDeliveryTracker(redisTracker, memoryCache, logger);
            });
        }
        else
        {
            // If no Redis, log warning and use a no-op implementation
            builder.Services.AddSingleton<ConduitLLM.Core.Interfaces.IWebhookDeliveryTracker>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<Program>>();
                logger.LogWarning("No Redis connection configured. Webhook delivery tracking and deduplication will not be available.");
                var noOpLogger = sp.GetRequiredService<ILogger<ConduitLLM.Gateway.Services.NoOpWebhookDeliveryTracker>>();
                return new ConduitLLM.Gateway.Services.NoOpWebhookDeliveryTracker(noOpLogger);
            });
        }
    }
}
