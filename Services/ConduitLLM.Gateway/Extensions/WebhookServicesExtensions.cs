using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Handlers;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Services.SpendNotification;
using Microsoft.AspNetCore.SignalR;
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
        services.AddWebhookHttpServices(configuration);

        // Register Distributed Spend Notification Service (Redis-based for multi-instance consistency) - with leader election
        services.AddSingleton<ISpendNotificationService, DistributedSpendNotificationService>();
        services.AddLeaderElectedHostedService<DistributedSpendNotificationService>(
            sp => (DistributedSpendNotificationService)sp.GetRequiredService<ISpendNotificationService>(),
            "SpendNotificationService");

        services.AddScoped<IWebhookRecovery, ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore>();

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

        return services;
    }

    /// <summary>Production HTTP sender and receiver retry policy, independently testable with real receivers.</summary>
    public static IServiceCollection AddWebhookHttpServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebhookDeliveryOptions>()
            .Bind(configuration.GetSection(WebhookDeliveryOptions.SectionName))
            .Validate(options => options.IsValid(), "Invalid webhook delivery options.")
            .ValidateOnStart();
        services.AddSingleton<WebhookDeliveryPolicy>();
        services.AddSingleton<IWebhookAdmission, WebhookAdmission>();
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
