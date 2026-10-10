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

        // Data Protection reuses the DI-owned multiplexer registered above.
        builder.Services.AddRedisDataProtection(redisConnectionString, "Conduit");

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

            // Register one scoped implementation behind separate request-time and
            // management contracts. Native builds can replace only the runtime
            // contract without rooting management CRUD in the data plane.
            builder.Services.AddScoped<CachedApiVirtualKeyService>(serviceProvider =>
            {
                var virtualKeyRepository = serviceProvider.GetRequiredService<IVirtualKeyRepository>();
                var spendHistoryRepository = serviceProvider.GetRequiredService<IVirtualKeySpendHistoryRepository>();
                var groupRepository = serviceProvider.GetRequiredService<IVirtualKeyGroupRepository>();
                var cache = serviceProvider.GetRequiredService<ConduitLLM.Core.Interfaces.IVirtualKeyCache>();
                var eventBus = serviceProvider.GetService<ConduitLLM.Configuration.Messaging.IEventBus>(); // Optional
                var logger = serviceProvider.GetRequiredService<ILogger<CachedApiVirtualKeyService>>();

                return new CachedApiVirtualKeyService(virtualKeyRepository, spendHistoryRepository, groupRepository, cache, eventBus, logger);
            });
            builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IVirtualKeyService>(serviceProvider =>
                serviceProvider.GetRequiredService<CachedApiVirtualKeyService>());
        }
        else
        {
            // Fall back to direct database Virtual Key service
            builder.Services.AddScoped<DirectApiVirtualKeyService>(sp =>
            {
                var virtualKeyRepository = sp.GetRequiredService<IVirtualKeyRepository>();
                var groupRepository = sp.GetRequiredService<IVirtualKeyGroupRepository>();
                var spendHistoryRepository = sp.GetRequiredService<IVirtualKeySpendHistoryRepository>();
                var eventBus = sp.GetService<ConduitLLM.Configuration.Messaging.IEventBus>(); // Optional
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Gateway.Services.DirectApiVirtualKeyService>>();
                return new DirectApiVirtualKeyService(
                    virtualKeyRepository, groupRepository, spendHistoryRepository, eventBus, logger);
            });
            builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IVirtualKeyService>(sp =>
                sp.GetRequiredService<DirectApiVirtualKeyService>());
        }

#if CONDUIT_NATIVE_AOT
        // Native requests use fixed-shape persistence; management keeps its EF contract.
        builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IVirtualKeyRuntimeService,
            StoreBackedVirtualKeyRuntimeService>();
#else
        builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IVirtualKeyRuntimeService>(sp =>
            sp.GetRequiredService<ConduitLLM.Core.Interfaces.IVirtualKeyService>());
#endif
    }
}
