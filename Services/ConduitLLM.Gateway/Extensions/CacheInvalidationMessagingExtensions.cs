using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;

using Microsoft.Extensions.DependencyInjection;

using Wolverine;

namespace ConduitLLM.Gateway.Extensions
{
    /// <summary>
    /// Registration for the low-risk cache-invalidation / notification handlers migrated to
    /// <c>IEventHandler&lt;T&gt;</c> in epic #909 (issue #919). Centralizes the handler DI
    /// registrations and the matching bridge handlers so the Gateway and Admin
    /// hosts stay in sync. These handlers are idempotent and dispatched on the default
    /// (auto-configured) endpoints, exactly as their <c>IConsumer&lt;T&gt;</c> predecessors were.
    /// </summary>
    public static class CacheInvalidationMessagingExtensions
    {
        /// <summary>
        /// Registers the Gateway-hosted cache-invalidation/notification handlers and their
        /// bridges (the handlers that run only in the Gateway process).
        /// </summary>
        public static IServiceCollection AddGatewayCacheInvalidationHandlers(this IServiceCollection services)
        {
            // VirtualKey cache invalidation (one class, four event types)
            services.AddEventHandler<VirtualKeyUpdated, Gateway.EventHandlers.VirtualKeyCacheInvalidationHandler>();
            services.AddEventHandler<VirtualKeyCreated, Gateway.EventHandlers.VirtualKeyCacheInvalidationHandler>();
            services.AddEventHandler<VirtualKeyDeleted, Gateway.EventHandlers.VirtualKeyCacheInvalidationHandler>();
            services.AddEventHandler<SpendUpdated, Gateway.EventHandlers.VirtualKeyCacheInvalidationHandler>();

            // Spend notification (SignalR)
            services.AddEventHandler<SpendUpdated, Gateway.EventHandlers.SpendUpdatedHandler>();
            services.AddEventHandler<SpendThresholdExceeded, Gateway.EventHandlers.SpendThresholdAlertHandler>();

            // Provider events: capability refresh + cache invalidation (two classes)
            services.AddEventHandler<ProviderUpdated, Gateway.EventHandlers.ProviderEventHandler>();
            services.AddEventHandler<ProviderDeleted, Gateway.EventHandlers.ProviderEventHandler>();
            services.AddEventHandler<ProviderCreated, Gateway.EventHandlers.ProviderCacheInvalidationHandler>();
            services.AddEventHandler<ProviderUpdated, Gateway.EventHandlers.ProviderCacheInvalidationHandler>();
            services.AddEventHandler<ProviderDeleted, Gateway.EventHandlers.ProviderCacheInvalidationHandler>();

            // Model / discovery / async-task / media / video-started caches
            services.AddEventHandler<ModelUpdated, Gateway.EventHandlers.ModelCacheInvalidationHandler>();
            services.AddEventHandler<DiscoveryCacheInvalidationRequested, Gateway.EventHandlers.DiscoveryCacheInvalidationHandler>();
            services.AddEventHandler<AsyncTaskCreated, Gateway.EventHandlers.AsyncTaskCacheInvalidationHandler>();
            services.AddEventHandler<AsyncTaskUpdated, Gateway.EventHandlers.AsyncTaskCacheInvalidationHandler>();
            services.AddEventHandler<AsyncTaskDeleted, Gateway.EventHandlers.AsyncTaskCacheInvalidationHandler>();
            services.AddEventHandler<MediaGenerationCompleted, Gateway.EventHandlers.MediaLifecycleHandler>();
            services.AddEventHandler<VideoGenerationStarted, Gateway.EventHandlers.VideoGenerationStartedHandler>();
            services.AddEventHandler<MediaCleanupAlertRaised, Gateway.EventHandlers.MediaCleanupAlertHandler>();

            // Model-mapping / model-cost / ip-filter / provider-tool caches
            services.AddEventHandler<ModelMappingChanged, Gateway.Consumers.ModelMappingCacheInvalidationHandler>();
            services.AddEventHandler<ModelCostChanged, Gateway.Consumers.ModelCostCacheInvalidationHandler>();
            services.AddEventHandler<IpFilterChanged, Gateway.Consumers.IpFilterCacheInvalidationHandler>();
            services.AddEventHandler<ProviderToolChanged, Gateway.Consumers.ProviderToolCacheInvalidationHandler>();

            // Provider key-credential cache invalidation (one class, four event types).
            // These events live in ConduitLLM.Configuration.Events (that is what the Admin
            // publish sites emit), unlike the rest of this file which uses Core.Events.
            services.AddEventHandler<Configuration.Events.ProviderKeyCredentialCreated, Gateway.EventHandlers.ProviderKeyCredentialCacheInvalidationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyCredentialUpdated, Gateway.EventHandlers.ProviderKeyCredentialCacheInvalidationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyCredentialDeleted, Gateway.EventHandlers.ProviderKeyCredentialCacheInvalidationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyCredentialPrimaryChanged, Gateway.EventHandlers.ProviderKeyCredentialCacheInvalidationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyDisabledEvent, Gateway.EventHandlers.ProviderKeyCredentialCacheInvalidationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyReenabledEvent, Gateway.EventHandlers.ProviderKeyCredentialCacheInvalidationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyDisabledEvent, Gateway.EventHandlers.ProviderKeyStatusNotificationHandler>();
            services.AddEventHandler<Configuration.Events.ProviderKeyReenabledEvent, Gateway.EventHandlers.ProviderKeyStatusNotificationHandler>();

            return services;
        }

        /// <summary>
        /// The event types bridged to the Gateway-hosted cache-invalidation/notification
        /// handlers. One list drives both backends' bridge registration so they cannot
        /// drift (epic #909 Phase 2, #925); the canonical list lives in
        /// <see cref="ConduitLLM.Core.Messaging.ConduitMessagingTopology"/> because the
        /// Wolverine queue routing (#926) needs it from both hosts.
        /// </summary>
        public static IReadOnlyList<Type> BridgedEventTypes =>
            ConduitLLM.Core.Messaging.ConduitMessagingTopology.GatewayCacheInvalidationEvents;

        /// <summary>
        /// Registers the Wolverine bridge handlers for the Gateway-hosted cache events (#925).
        /// </summary>
        public static void AddGatewayCacheInvalidationBridges(this WolverineOptions options)
        {
            options.AddEventBridge<VirtualKeyUpdated>();
            options.AddEventBridge<VirtualKeyCreated>();
            options.AddEventBridge<VirtualKeyDeleted>();
            options.AddEventBridge<SpendUpdated>();
            options.AddEventBridge<SpendThresholdExceeded>();
            options.AddEventBridge<ProviderCreated>();
            options.AddEventBridge<ProviderUpdated>();
            options.AddEventBridge<ProviderDeleted>();
            options.AddEventBridge<ModelUpdated>();
            options.AddEventBridge<DiscoveryCacheInvalidationRequested>();
            options.AddEventBridge<AsyncTaskCreated>();
            options.AddEventBridge<AsyncTaskUpdated>();
            options.AddEventBridge<AsyncTaskDeleted>();
            options.AddEventBridge<MediaGenerationCompleted>();
            options.AddEventBridge<VideoGenerationStarted>();
            options.AddEventBridge<MediaCleanupAlertRaised>();
            options.AddEventBridge<ModelMappingChanged>();
            options.AddEventBridge<ModelCostChanged>();
            options.AddEventBridge<IpFilterChanged>();
            options.AddEventBridge<ProviderToolChanged>();
            options.AddEventBridge<Configuration.Events.ProviderKeyCredentialCreated>();
            options.AddEventBridge<Configuration.Events.ProviderKeyCredentialUpdated>();
            options.AddEventBridge<Configuration.Events.ProviderKeyCredentialDeleted>();
            options.AddEventBridge<Configuration.Events.ProviderKeyCredentialPrimaryChanged>();
            options.AddEventBridge<Configuration.Events.ProviderKeyDisabledEvent>();
            options.AddEventBridge<Configuration.Events.ProviderKeyReenabledEvent>();
        }
    }
}
