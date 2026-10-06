using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services;

public partial class Program
{
    public static void ConfigureMessagingServices(WebApplicationBuilder builder)
    {
        // Backend-neutral handler registrations.
        //
        // Cache-invalidation / notification IEventHandler<T> implementations (#919):
        ConduitLLM.Gateway.Extensions.CacheInvalidationMessagingExtensions.AddGatewayCacheInvalidationHandlers(builder.Services);
        ConduitLLM.Core.Extensions.SharedCacheInvalidationMessagingExtensions.AddSharedCacheInvalidationHandlers(builder.Services);

        // Media-generation orchestrator / notification IEventHandler<T> implementations (#920);
        // the orchestrator bridges bind to the tuned image-/video-generation queues below.
        ConduitLLM.Gateway.Extensions.MediaGenerationMessagingExtensions.AddMediaGenerationHandlers(builder.Services);

        // High-risk handlers (#921): ordered spend processing (spend-update-events),
        // deferred-retry webhook delivery (webhook-delivery), and the batch spend flush.
        builder.Services.AddEventHandler<ConduitLLM.Core.Events.SpendUpdateRequested, ConduitLLM.Gateway.EventHandlers.SpendUpdateProcessor>();
        builder.Services.AddEventHandler<ConduitLLM.Configuration.Events.BatchSpendFlushRequestedEvent, ConduitLLM.Gateway.EventHandlers.BatchSpendFlushRequestedHandler>();
        builder.Services.AddEventHandler<ConduitLLM.Core.Events.WebhookDeliveryRequested, ConduitLLM.Gateway.Consumers.WebhookDeliveryConsumer>();

        // Wolverine on the PostgreSQL transport is the only messaging backend as of I3.1
        // (#932, epic #909); the previous backend was removed after the cutover (#930)
        // soaked. Resolve still runs so a stale rollback backend value fails the boot with a
        // clear pointer to Wolverine (see MessagingBackendResolver) instead of being silently
        // ignored.
        _ = MessagingBackendResolver.Resolve(builder.Configuration);
        ConfigureWolverineMessaging(builder);
    }

    /// <summary>
    /// Wolverine backend wiring (#925/#926): IEventBus adapter + one bridge handler per
    /// bridged event type, on the PostgreSQL transport with durable persistence. Events
    /// route through the shared queue topology (<c>ConduitMessagingTopology</c>): the four
    /// tuned queues carry the <c>ConduitEndpointPolicies</c> descriptors translated by
    /// <c>WolverineEndpointPolicy</c> (strict ordering for spend/image, concurrency cap +
    /// circuit breaker for webhooks, per-type retry rules), everything else rides
    /// <c>gateway-events</c>. Cross-service delivery (Admin→Gateway) flows over the same
    /// queues.
    /// </summary>
    private static void ConfigureWolverineMessaging(WebApplicationBuilder builder)
    {
        builder.Services.AddWolverineEventBus();

        var (_, connectionString) = new ConduitLLM.Core.Data.ConnectionStringManager()
            .GetProviderAndConnectionString("CoreAPI");

        var postgresTransport = !WolverineMessagingExtensions.UsesInMemoryTransport(builder.Configuration);

        builder.Host.AddConduitWolverine(builder.Configuration, connectionString, "conduit-gateway", opts =>
        {
            // AddConduitWolverine is declared in the shared configuration assembly, so
            // Wolverine's calling-assembly inference can otherwise point static loading at
            // ConduitLLM.Configuration. The committed adapters are compiled into this host.
            opts.ApplicationAssembly = typeof(Program).Assembly;

            opts.UseSystemTextJsonForSerialization(options =>
            {
                options.TypeInfoResolverChain.Insert(0, CoreMessagingJsonContext.Default);
            });

            ConduitLLM.Gateway.Extensions.CacheInvalidationMessagingExtensions.AddGatewayCacheInvalidationBridges(opts);
            ConduitLLM.Core.Extensions.SharedCacheInvalidationMessagingExtensions.AddSharedCacheInvalidationBridges(opts);
            ConduitLLM.Gateway.Extensions.MediaGenerationMessagingExtensions.AddMediaGenerationBridges(opts);

            // High-risk bridges (#921); their endpoint tuning is applied by
            // ListenAsConduitGateway below (#926).
            opts.AddEventBridge<SpendUpdateRequested>();
            opts.AddEventBridge<WebhookDeliveryRequested>();
            opts.AddEventBridge<ConduitLLM.Configuration.Events.BatchSpendFlushRequestedEvent>();

            // Event→queue topology (#926): publish routing identical on all hosts;
            // the Gateway listens on the four tuned queues + gateway-events. Postgres
            // queues only exist on the Postgresql transport — in-memory mode (dev/CI,
            // #928) routes everything to local queues instead.
            if (postgresTransport)
            {
                ConduitLLM.Core.Messaging.ConduitMessagingTopology.ApplyConduitPublishRouting(opts);
                ConduitLLM.Core.Messaging.ConduitMessagingTopology.ListenAsConduitGateway(opts);
            }
        });

        // Batch webhook publisher: publishes via IEventBus, so it is backend-agnostic.
        builder.Services.AddBatchWebhookPublisher(options =>
        {
            options.MaxBatchSize = 100;
            options.MaxBatchDelay = TimeSpan.FromMilliseconds(100);
            options.ConcurrentPublishers = 3;
        });

        // Gateway liveness heartbeat (#1067): every instance publishes a GatewayHeartbeat via
        // IEventBus so the Admin health dashboard reports the Gateway's real status from
        // staleness (keeps Admin↔Gateway event-only — no synchronous HTTP probe). Registered
        // with a plain AddHostedService — NOT leader-elected — because a per-instance liveness
        // signal must be emitted by every instance.
        builder.Services.AddHostedService<ConduitLLM.Gateway.Services.GatewayHeartbeatPublisher>();
    }
}
