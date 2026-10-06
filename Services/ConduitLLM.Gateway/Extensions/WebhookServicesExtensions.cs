using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Handlers;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Services.SpendNotification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using ConduitLLM.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Extension methods for registering webhook-related services
/// </summary>
public static class WebhookServicesExtensions
{
    /// <summary>
    /// Adds webhook services including delivery, metrics, connection tracking, and circuit breakers
    /// </summary>
    public static IServiceCollection AddWebhookServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebhookDeliveryOptions>()
            .Bind(configuration.GetSection(WebhookDeliveryOptions.SectionName))
            .Validate(options => options.IsValid(), "Invalid webhook delivery options.")
            .ValidateOnStart();
        // Register Webhook Delivery Service
        services.AddSingleton<IWebhookDeliveryService, WebhookDeliveryService>();

        // Register Distributed Spend Notification Service (Redis-based for multi-instance consistency) - with leader election
        services.AddSingleton<ISpendNotificationService, DistributedSpendNotificationService>();
        services.AddLeaderElectedHostedService<DistributedSpendNotificationService>(
            sp => (DistributedSpendNotificationService)sp.GetRequiredService<ISpendNotificationService>(),
            "SpendNotificationService");

        // Register Webhook Metrics Service (Redis-based when available)
        services.AddSingleton<ConduitLLM.Core.Services.IWebhookMetricsService>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();

            if (redis != null)
            {
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Core.Services.RedisWebhookMetricsService>>();
                return new ConduitLLM.Core.Services.RedisWebhookMetricsService(redis, logger);
            }

            // Return null when Redis is not available - the notification service will handle fallback
            return null!;
        });

        // Register Webhook Connection Tracker (Redis-based when available)
        services.AddSingleton<ConduitLLM.Core.Services.IWebhookConnectionTracker>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();

            if (redis != null)
            {
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Core.Services.RedisWebhookConnectionTracker>>();
                return new ConduitLLM.Core.Services.RedisWebhookConnectionTracker(redis, logger);
            }
            else
            {
                // Fall back to in-memory tracker
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Core.Services.InMemoryWebhookConnectionTracker>>();
                return new ConduitLLM.Core.Services.InMemoryWebhookConnectionTracker(logger);
            }
        });

        // Register Webhook Delivery Notification Service - with leader election
        services.AddSingleton<IWebhookDeliveryNotificationService>(sp =>
        {
            var hubContext = sp.GetRequiredService<IHubContext<Hubs.WebhookDeliveryHub>>();
            var serviceProvider = sp;
            var logger = sp.GetRequiredService<ILogger<WebhookDeliveryNotificationService>>();
            return new WebhookDeliveryNotificationService(hubContext, serviceProvider, logger);
        });
        services.AddLeaderElectedHostedService<WebhookDeliveryNotificationService>(
            sp => (WebhookDeliveryNotificationService)sp.GetRequiredService<IWebhookDeliveryNotificationService>(),
            "WebhookDeliveryNotificationService");

        // Register Webhook Circuit Breaker for preventing repeated failures
        services.AddSingleton<ConduitLLM.Core.Services.IWebhookCircuitBreaker>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();

            if (redis != null)
            {
                // Use Redis-based distributed circuit breaker when available
                var redisLogger = sp.GetRequiredService<ILogger<ConduitLLM.Core.Services.RedisWebhookCircuitBreaker>>();
                return new ConduitLLM.Core.Services.RedisWebhookCircuitBreaker(
                    redis,
                    redisLogger,
                    failureThreshold: 5,
                    openDuration: TimeSpan.FromMinutes(5),
                    halfOpenTestInterval: TimeSpan.FromSeconds(30));
            }
            else
            {
                // Fall back to in-memory circuit breaker
                var cache = sp.GetRequiredService<IMemoryCache>();
                var logger = sp.GetRequiredService<ILogger<ConduitLLM.Core.Services.WebhookCircuitBreaker>>();

                return new ConduitLLM.Core.Services.WebhookCircuitBreaker(
                    cache,
                    logger,
                    failureThreshold: 5,
                    openDuration: TimeSpan.FromMinutes(5),
                    counterResetDuration: TimeSpan.FromMinutes(15));
            }
        });

        // Register Webhook Notification Service with optimized configuration for high throughput
        services.AddTransient<WebhookMetricsHandler>();
        services.AddHttpClient<IWebhookNotificationService, WebhookNotificationService>(
            "WebhookClient",
            client =>
            {
                // The sender applies one per-attempt deadline, including custom timeouts.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.Add("User-Agent", "Conduit-LLM/1.0");
                client.DefaultRequestHeaders.ConnectionClose = false;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 100,
                EnableMultipleHttp2Connections = true,
                MaxResponseHeadersLength = 64,
                MaxResponseDrainSize = 0,
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value.ConnectTimeoutSeconds),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30)
            })
            .AddHttpMessageHandler<WebhookMetricsHandler>();

        return services;
    }

}
