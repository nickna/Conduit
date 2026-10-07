using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Interfaces;
using Microsoft.AspNetCore.SignalR;

public partial class Program
{
    public static void ConfigureSignalRServices(WebApplicationBuilder builder)
    {
        // Get Redis connection string from environment
        var redisConnectionString = ConduitLLM.Configuration.Utilities.RedisUrlParser.ResolveConnectionString();

        // Register VirtualKeyHubFilter for SignalR authentication
        builder.Services.AddScoped<ConduitLLM.Gateway.Authentication.VirtualKeyHubFilter>();

        // Register Redis-based distributed rate limiting services
        // Check if Redis is available
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            // Register the Redis-based virtual key rate limit service
            builder.Services.AddSingleton<ConduitLLM.Core.Services.IVirtualKeyRateLimitService, ConduitLLM.Core.Services.RedisVirtualKeyRateLimitService>();
            
            // Register the Redis-based SignalR rate limit service
            builder.Services.AddSingleton<ConduitLLM.Core.Services.ISignalRRateLimitService, ConduitLLM.Core.Services.RedisSignalRRateLimitService>();
            
        }
        else
        {
            // If no Redis, create a warning and provide a fallback
            builder.Services.AddSingleton<ConduitLLM.Core.Services.ISignalRRateLimitService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<Program>>();
                logger.LogWarning("No Redis connection configured. SignalR rate limiting will fall back to local memory (security risk in multi-instance deployments)");
                // For now, throw an exception to enforce Redis requirement for rate limiting
                throw new InvalidOperationException("Redis is required for secure distributed rate limiting. Please configure REDIS_URL or CONDUIT_REDIS_CONNECTION_STRING.");
            });
            
            builder.Services.AddSingleton<ConduitLLM.Core.Services.IVirtualKeyRateLimitService>(sp =>
            {
                throw new InvalidOperationException("Redis is required for secure distributed rate limiting. Please configure REDIS_URL or CONDUIT_REDIS_CONNECTION_STRING.");
            });
        }

        // Register SignalR rate limit filter
        builder.Services.AddSingleton<ConduitLLM.Gateway.Authentication.VirtualKeySignalRRateLimitFilter>();

        // Register SignalR metrics
        builder.Services.AddSingleton<ConduitLLM.Gateway.Metrics.SignalRMetrics>();
        builder.Services.AddSingleton<ConduitLLM.Gateway.Interfaces.ISignalRMetrics>(sp => sp.GetRequiredService<ConduitLLM.Gateway.Metrics.SignalRMetrics>());

        // Register SignalR metrics filter
        builder.Services.AddSingleton<ConduitLLM.Gateway.Filters.SignalRMetricsFilter>();

        // Register SignalR error handling filter
        builder.Services.AddSingleton<ConduitLLM.Gateway.Filters.SignalRErrorHandlingFilter>();

        // Register SignalR authentication service
        builder.Services.AddScoped<ConduitLLM.Gateway.Authentication.ISignalRAuthenticationService, ConduitLLM.Gateway.Authentication.SignalRAuthenticationService>();

        // Add SignalR with shared configuration (MessagePack, Redis backplane)
        var signalRRedisConnectionString = builder.Configuration.GetConnectionString("RedisSignalR") ?? redisConnectionString;
        builder.Services.AddConduitSignalR(
            builder.Environment,
            signalRRedisConnectionString,
            redisChannelPrefix: "conduit_signalr:",
            redisDatabase: 2,
            serviceName: "Conduit",
            configureHubOptions: options =>
            {
                options.AddFilter<ConduitLLM.Gateway.Filters.SignalRMetricsFilter>();
                options.AddFilter<ConduitLLM.Gateway.Filters.SignalRErrorHandlingFilter>();
                options.AddFilter<ConduitLLM.Gateway.Authentication.VirtualKeyHubFilter>();
                options.AddFilter<ConduitLLM.Gateway.Authentication.VirtualKeySignalRRateLimitFilter>();
            });

        // Register settings refresh service for runtime configuration updates
        builder.Services.AddSingleton<ISettingsRefreshService, SettingsRefreshService>();

        // Register video generation notification service
        builder.Services.AddSingleton<IVideoGenerationNotificationService, VideoGenerationNotificationService>();

        // Register image generation notification service
        builder.Services.AddSingleton<IImageGenerationNotificationService, ImageGenerationNotificationService>();

        // Register virtual key management notification service
        builder.Services.AddSingleton<IVirtualKeyManagementNotificationService, VirtualKeyManagementNotificationService>();

        // Register billing alerting service for critical failure notifications
        builder.Services.AddSingleton<ConduitLLM.Configuration.Interfaces.IBillingAlertingService, ConduitLLM.Configuration.Services.BillingAlertingService>();

        // Register Redis circuit breaker configuration
        builder.Services.Configure<ConduitLLM.Configuration.Options.RedisCircuitBreakerOptions>(
            builder.Configuration.GetSection("RedisCircuitBreaker"));

        // Register SignalR connection limit configuration
        builder.Services.Configure<SignalRConnectionOptions>(
            builder.Configuration.GetSection(SignalRConnectionOptions.SectionName));

        // Register Redis circuit breaker service
        builder.Services.AddSingleton<ConduitLLM.Configuration.Interfaces.IRedisCircuitBreaker, ConduitLLM.Configuration.Services.RedisCircuitBreaker>();

        // Register batch spend update service for optimized Virtual Key operations
        // Use factory to prevent auto-discovery by ASP.NET Core - register ONLY via interface
        builder.Services.AddSingleton<IBatchSpendUpdateService>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ConduitLLM.Configuration.Services.BatchSpendUpdateService>>();
            var serviceScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var redisConnectionFactory = serviceProvider.GetRequiredService<ConduitLLM.Configuration.Services.RedisConnectionFactory>();
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConduitLLM.Configuration.Options.BatchSpendingOptions>>();
            var alertingService = serviceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IBillingAlertingService>();
            var circuitBreaker = serviceProvider.GetService<ConduitLLM.Configuration.Interfaces.IRedisCircuitBreaker>();
            var batchService = new ConduitLLM.Configuration.Services.BatchSpendUpdateService(serviceScopeFactory, redisConnectionFactory, options, logger, alertingService, circuitBreaker);

            // Wire up cache invalidation event if Redis cache is available
            var cache = serviceProvider.GetService<ConduitLLM.Core.Interfaces.IVirtualKeyCache>();
            if (cache != null)
            {
                batchService.SpendUpdatesCompleted += async (keyHashes) =>
                {
                    try
                    {
                        await cache.InvalidateVirtualKeysAsync(keyHashes);
                        logger.LogDebug("Cache invalidated for {Count} Virtual Keys after batch spend update", keyHashes.Length);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to invalidate cache after batch spend update");
                    }
                };
            }

            return batchService;
        });
        builder.Services.AddLeaderElectedHostedService<ConduitLLM.Configuration.Services.BatchSpendUpdateService>(
            sp => {
                var service = (ConduitLLM.Configuration.Services.BatchSpendUpdateService)sp.GetRequiredService<IBatchSpendUpdateService>();
                return service;
            },
            "BatchSpendUpdateService");
    }
}
