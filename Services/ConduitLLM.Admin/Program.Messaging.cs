using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Admin;

public partial class Program
{
    /// <summary>
    /// Configures the event bus on the Wolverine backend (PostgreSQL transport, epic #909).
    /// Wolverine is the only supported backend as of I3.1 (#932); the previous backend was
    /// removed after the cutover (#930) soaked.
    /// </summary>
    private static void ConfigureMessagingServices(WebApplicationBuilder builder, ILogger startupLogger)
    {
        // Backend-neutral: the shared cache-invalidation IEventHandler<T> implementations (#919).
        ConduitLLM.Core.Extensions.SharedCacheInvalidationMessagingExtensions.AddSharedCacheInvalidationHandlers(builder.Services);

        // Gateway liveness heartbeat handler (#1067): records the Gateway's heartbeat so the
        // health dashboard reports the Gateway's real status. The matching bridge is added in
        // the AddConduitWolverine callback below.
        builder.Services.AddEventHandler<ConduitLLM.Core.Events.GatewayHeartbeat, ConduitLLM.Admin.EventHandlers.GatewayHeartbeatHandler>();

        // Wolverine on the PostgreSQL transport is the only messaging backend as of I3.1
        // (#932, epic #909). Resolve still runs so a stale rollback backend value fails the
        // boot with a clear pointer to Wolverine (see MessagingBackendResolver) instead of
        // being silently ignored.
        _ = MessagingBackendResolver.Resolve(builder.Configuration);

        builder.Services.AddWolverineEventBus();
        builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IWebhookRecovery, ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore>();
        builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IMediaTaskTerminalWriter, ConduitLLM.Messaging.Wolverine.MediaTaskTerminalWriter>();
        builder.Services.AddOptions<ConduitLLM.Core.Configuration.WebhookDeliveryOptions>()
            .Bind(builder.Configuration.GetSection(ConduitLLM.Core.Configuration.WebhookDeliveryOptions.SectionName))
            .Validate(o => o.IsValid(), "Invalid webhook delivery options.").ValidateOnStart();
        builder.Services.AddSingleton<ConduitLLM.Core.Services.WebhookDeliveryPolicy>();

        var (_, wolverineConnectionString) = new ConduitLLM.Core.Data.ConnectionStringManager()
            .GetProviderAndConnectionString("AdminAPI", msg => startupLogger.LogInformation("{Message}", msg));

        var postgresTransport = !WolverineMessagingExtensions.UsesInMemoryTransport(builder.Configuration);
        if (postgresTransport)
            builder.Services.AddSingleton(sp => new ConduitLLM.Messaging.Wolverine.WebhookErrorStore(
                sp.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(), wolverineConnectionString,
                builder.Configuration["Webhooks:GatewayDurabilitySchema"] ?? "wolverine_conduit_gateway"));

        builder.Host.AddConduitWolverine(builder.Configuration, wolverineConnectionString, "conduit-admin", opts =>
        {
            // AddConduitWolverine is declared in the shared configuration assembly, so
            // Wolverine's calling-assembly inference can otherwise point static loading at
            // ConduitLLM.Configuration. The committed adapters are compiled into this host.
            opts.ApplicationAssembly = typeof(Program).Assembly;

            opts.UseSystemTextJsonForSerialization(options =>
            {
                options.TypeInfoResolverChain.Insert(0, CoreMessagingJsonContext.Default);
            });

            ConduitLLM.Core.Extensions.SharedCacheInvalidationMessagingExtensions.AddSharedCacheInvalidationBridges(opts);

            // Gateway liveness heartbeat bridge (#1067). Registered unconditionally (like the
            // shared bridges above): on the in-memory transport the bridge alone routes the
            // event; on Postgres the topology routing below delivers it to admin-events.
            opts.AddEventBridge<ConduitLLM.Core.Events.GatewayHeartbeat>();

            // Event→queue topology (#926): the same publish routing as the Gateway
            // (so Admin publishes land on the Gateway's queues), listening only on
            // admin-events (the shared cache events). Postgres queues only exist on
            // the Postgresql transport — in-memory mode (dev/CI, #928) routes
            // everything to local queues instead.
            if (postgresTransport)
            {
                ConduitLLM.Core.Messaging.ConduitMessagingTopology.ApplyConduitPublishRouting(opts);
                ConduitLLM.Core.Messaging.ConduitMessagingTopology.ListenAsConduitAdmin(opts);
            }
        });

        startupLogger.LogInformation(
            postgresTransport
                ? "Event bus configured with the Wolverine backend (PostgreSQL transport, durable persistence, " +
                  "shared event->queue topology). Admin publishes route to the Gateway's tuned queues"
                : "Event bus configured with the Wolverine backend (in-memory transport, local queues only - dev/CI mode)");
    }
}
