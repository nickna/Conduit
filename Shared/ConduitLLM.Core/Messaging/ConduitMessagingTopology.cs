using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;

using Wolverine;
using Wolverine.Postgresql;

namespace ConduitLLM.Core.Messaging
{
    /// <summary>
    /// The canonical event→queue topology for the Wolverine backend (epic #909,
    /// I2.3/#926). Postgres queues are point-to-point, so the routing must be
    /// declared explicitly. This class is the single source of truth: publish routing is applied
    /// identically on every host (rules for types a host never publishes are inert), and
    /// each host listens only to its own queues.
    /// </summary>
    /// <remarks>
    /// Queue plan (all durable, in the shared Wolverine schema):
    /// <list type="bullet">
    /// <item>The four tuned queues from <see cref="ConduitEndpointPolicies"/>
    /// (webhook-delivery, spend-update-events, video-/image-generation-events) — Gateway
    /// listens with the translated policies.</item>
    /// <item><c>gateway-events</c> — every other Gateway-consumed event (cache
    /// invalidation, media progress/completed/failed, batch spend flush).</item>
    /// <item><c>admin-events</c> — the shared cache events Admin also consumes. Shared
    /// event types are routed to BOTH service queues (the fan-out RabbitMQ's exchange
    /// topology used to provide).</item>
    /// </list>
    /// Multiple instances of one service compete on their queue — the same
    /// one-consumer-per-event semantics as today's per-service RabbitMQ queues (per-instance
    /// cache fan-out remains the job of the separate Redis pub/sub cache bus).
    /// </remarks>
    public static class ConduitMessagingTopology
    {
        /// <summary>Default queue for Gateway-consumed events without a tuned policy.</summary>
        public const string GatewayEventsQueue = WolverineQueueNames.GatewayEvents;

        /// <summary>Queue for the shared cache events consumed by the Admin host.</summary>
        public const string AdminEventsQueue = WolverineQueueNames.AdminEvents;

        /// <summary>webhook-delivery (tuned: <see cref="ConduitEndpointPolicies.WebhookDelivery"/>).</summary>
        public static readonly IReadOnlyList<Type> WebhookDeliveryEvents = new[]
        {
            typeof(WebhookDeliveryRequested),
        };

        /// <summary>spend-update-events (tuned: <see cref="ConduitEndpointPolicies.SpendUpdate"/>).</summary>
        public static readonly IReadOnlyList<Type> SpendUpdateEvents = new[]
        {
            typeof(SpendUpdateRequested),
        };

        /// <summary>video-generation-events (tuned: <see cref="ConduitEndpointPolicies.VideoGeneration"/>).</summary>
        public static readonly IReadOnlyList<Type> VideoGenerationEvents = new[]
        {
            typeof(VideoGenerationRequested),
            typeof(VideoGenerationCancelled),
        };

        /// <summary>image-generation-events (tuned: <see cref="ConduitEndpointPolicies.ImageGeneration"/>).</summary>
        public static readonly IReadOnlyList<Type> ImageGenerationEvents = new[]
        {
            typeof(ImageGenerationRequested),
            typeof(ImageGenerationCancelled),
        };

        /// <summary>
        /// Gateway-hosted cache-invalidation / notification events (the Gateway's
        /// <c>CacheInvalidationMessagingExtensions</c> derives its bridge list from this).
        /// </summary>
        public static readonly IReadOnlyList<Type> GatewayCacheInvalidationEvents = new[]
        {
            typeof(VirtualKeyUpdated),
            typeof(VirtualKeyCreated),
            typeof(VirtualKeyDeleted),
            typeof(SpendUpdated),
            typeof(SpendThresholdExceeded),
            typeof(ProviderCreated),
            typeof(ProviderUpdated),
            typeof(ProviderDeleted),
            typeof(ModelUpdated),
            typeof(DiscoveryCacheInvalidationRequested),
            typeof(AsyncTaskCreated),
            typeof(AsyncTaskUpdated),
            typeof(AsyncTaskDeleted),
            typeof(MediaGenerationCompleted),
            typeof(VideoGenerationStarted),
            typeof(MediaCleanupAlertRaised),
            typeof(ModelMappingChanged),
            typeof(ModelCostChanged),
            typeof(IpFilterChanged),
            typeof(ProviderToolChanged),
            typeof(ConduitLLM.Configuration.Events.ProviderKeyCredentialCreated),
            typeof(ConduitLLM.Configuration.Events.ProviderKeyCredentialUpdated),
            typeof(ConduitLLM.Configuration.Events.ProviderKeyCredentialDeleted),
            typeof(ConduitLLM.Configuration.Events.ProviderKeyCredentialPrimaryChanged),
            typeof(ConduitLLM.Configuration.Events.ProviderKeyDisabledEvent),
            typeof(ConduitLLM.Configuration.Events.ProviderKeyReenabledEvent),
        };

        /// <summary>
        /// Media-generation notification events consumed on the default endpoint (the
        /// orchestrator request/cancel/progress-check types ride the tuned queues above).
        /// </summary>
        public static readonly IReadOnlyList<Type> MediaGenerationDefaultEvents = new[]
        {
            typeof(IndeterminateMediaTaskRetryRequested),
            typeof(ImageGenerationProgress),
            typeof(ImageGenerationCompleted),
            typeof(ImageGenerationFailed),
            typeof(VideoGenerationProgress),
            typeof(VideoGenerationCompleted),
            typeof(VideoGenerationFailed),
        };

        /// <summary>
        /// Everything routed to <see cref="GatewayEventsQueue"/>: cache invalidation,
        /// media notifications, and the batch spend flush.
        /// </summary>
        public static readonly IReadOnlyList<Type> GatewayEvents =
            GatewayCacheInvalidationEvents
                .Concat(MediaGenerationDefaultEvents)
                .Append(typeof(ConduitLLM.Configuration.Events.BatchSpendFlushRequestedEvent))
                .ToArray();

        /// <summary>
        /// Shared cache events consumed by BOTH hosts — routed to both service queues.
        /// </summary>
        public static IReadOnlyList<Type> SharedEvents =>
            SharedCacheInvalidationMessagingExtensions.BridgedEventTypes;

        /// <summary>
        /// Gateway→Admin liveness heartbeat (#1067). Admin-only, so it rides the default
        /// <see cref="AdminEventsQueue"/> that Admin already listens on — no new queue or
        /// listener needed.
        /// </summary>
        public static readonly IReadOnlyList<Type> AdminHeartbeatEvents = new[]
        {
            typeof(GatewayHeartbeat),
        };

        /// <summary>
        /// Declares the publish routing rules. Applied identically on every host so an
        /// event lands on its consumer's queue no matter which service publishes it.
        /// </summary>
        public static void ApplyConduitPublishRouting(this WolverineOptions options)
        {
            RouteAll(options, WebhookDeliveryEvents, ConduitEndpointPolicies.WebhookDelivery.Name);
            RouteAll(options, SpendUpdateEvents, ConduitEndpointPolicies.SpendUpdate.Name);
            RouteAll(options, VideoGenerationEvents, ConduitEndpointPolicies.VideoGeneration.Name);
            RouteAll(options, ImageGenerationEvents, ConduitEndpointPolicies.ImageGeneration.Name);
            RouteAll(options, GatewayEvents, GatewayEventsQueue);

            // Fan-out: shared cache events go to both services' queues.
            RouteAll(options, SharedEvents, GatewayEventsQueue);
            RouteAll(options, SharedEvents, AdminEventsQueue);

            // Gateway→Admin liveness heartbeat (#1067): published by the Gateway, consumed by
            // Admin on its existing queue.
            RouteAll(options, AdminHeartbeatEvents, AdminEventsQueue);
        }

        /// <summary>
        /// Gateway-side listeners: the four tuned queues with their translated policies
        /// plus the default <see cref="GatewayEventsQueue"/>.
        /// </summary>
        public static void ListenAsConduitGateway(this WolverineOptions options)
        {
            options.ListenWithPolicy(ConduitEndpointPolicies.WebhookDelivery, WebhookDeliveryEvents);
            options.ListenWithPolicy(ConduitEndpointPolicies.SpendUpdate, SpendUpdateEvents);
            options.ListenWithPolicy(ConduitEndpointPolicies.VideoGeneration, VideoGenerationEvents);
            options.ListenWithPolicy(ConduitEndpointPolicies.ImageGeneration, ImageGenerationEvents);

            // Default queue: same aggressive polling as the tuned endpoints — the
            // listener's 20-per-5s poll default is a ~4 msg/s ceiling (#929 finding W4).
            options.ListenToPostgresqlQueue(GatewayEventsQueue)
                .PollingInterval(TimeSpan.FromMilliseconds(250))
                .MaximumMessagesToReceive(50);
        }

        /// <summary>Admin-side listener: the shared cache events queue.</summary>
        public static void ListenAsConduitAdmin(this WolverineOptions options)
        {
            options.ListenToPostgresqlQueue(AdminEventsQueue)
                .PollingInterval(TimeSpan.FromMilliseconds(250))
                .MaximumMessagesToReceive(50);
        }

        private static void RouteAll(WolverineOptions options, IReadOnlyList<Type> eventTypes, string queueName)
        {
            foreach (var eventType in eventTypes)
            {
                options.PublishMessage(eventType).ToPostgresqlQueue(queueName);
            }
        }
    }
}
