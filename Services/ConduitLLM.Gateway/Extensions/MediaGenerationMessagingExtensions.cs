using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;

using Microsoft.Extensions.DependencyInjection;

using Wolverine;

namespace ConduitLLM.Gateway.Extensions
{
    /// <summary>
    /// Registration for the media-generation orchestrators and their progress/completed/failed
    /// handlers, migrated to <c>IEventHandler&lt;T&gt;</c> in epic #909 (issue #920). The
    /// orchestrators (<c>ImageGenerationOrchestrator</c>, <c>VideoGenerationOrchestrator</c>,
    /// <c>VideoProgressTrackingOrchestrator</c>) consume on the tuned
    /// <c>image-generation-events</c> / <c>video-generation-events</c> endpoints — their
    /// bridges are bound there explicitly by <c>Program.Messaging.cs</c> — while the
    /// progress/completed/failed handlers ride the default (auto-configured) endpoints,
    /// exactly as their <c>IConsumer&lt;T&gt;</c> predecessors did.
    /// </summary>
    public static class MediaGenerationMessagingExtensions
    {
        /// <summary>
        /// Registers the media-generation <c>IEventHandler&lt;T&gt;</c> implementations.
        /// </summary>
        public static IServiceCollection AddMediaGenerationHandlers(this IServiceCollection services)
        {
            // Image generation orchestrator (request + cancellation on image-generation-events)
            services.AddEventHandler<ImageGenerationRequested, ConduitLLM.Core.Services.ImageGenerationOrchestrator>();
            services.AddEventHandler<ImageGenerationCancelled, ConduitLLM.Core.Services.ImageGenerationOrchestrator>();

            // Video generation orchestrators (request + cancellation + progress checks on video-generation-events)
            services.AddEventHandler<VideoGenerationRequested, ConduitLLM.Core.Services.VideoGenerationOrchestrator>();
            services.AddEventHandler<VideoGenerationCancelled, ConduitLLM.Core.Services.VideoGenerationOrchestrator>();
            services.AddEventHandler<VideoProgressCheckRequested, ConduitLLM.Core.Services.VideoProgressTrackingOrchestrator>();

            // Progress / completed / failed notification handlers (default endpoints)
            services.AddEventHandler<ImageGenerationProgress, Gateway.EventHandlers.ImageGenerationProgressHandler>();
            services.AddEventHandler<ImageGenerationCompleted, Gateway.EventHandlers.ImageGenerationCompletedHandler>();
            services.AddEventHandler<ImageGenerationFailed, Gateway.EventHandlers.ImageGenerationFailedHandler>();
            services.AddEventHandler<VideoGenerationProgress, Gateway.EventHandlers.VideoGenerationProgressHandler>();
            services.AddEventHandler<VideoGenerationCompleted, Gateway.EventHandlers.VideoGenerationCompletedHandler>();
            services.AddEventHandler<VideoGenerationFailed, Gateway.EventHandlers.VideoGenerationFailedHandler>();
            services.AddEventHandler<IndeterminateMediaTaskRetryRequested,
                Gateway.EventHandlers.IndeterminateMediaTaskRetryRequestedHandler>();

            return services;
        }

        /// <summary>
        /// The media-generation event types bridged to handlers. One list drives both
        /// backends' bridge registration so they cannot drift (epic #909 Phase 2, #925);
        /// the canonical per-queue lists live in
        /// <see cref="ConduitLLM.Core.Messaging.ConduitMessagingTopology"/> because the
        /// Wolverine queue routing (#926) needs them from both hosts.
        /// </summary>
        public static readonly IReadOnlyList<Type> BridgedEventTypes =
            ConduitLLM.Core.Messaging.ConduitMessagingTopology.ImageGenerationEvents
                .Concat(ConduitLLM.Core.Messaging.ConduitMessagingTopology.VideoGenerationEvents)
                .Concat(ConduitLLM.Core.Messaging.ConduitMessagingTopology.MediaGenerationDefaultEvents)
                .ToArray();

        /// <summary>
        /// Registers the Wolverine bridge handlers for the media-generation events (#925).
        /// </summary>
        public static void AddMediaGenerationBridges(this WolverineOptions options)
        {
            options.AddEventBridge<ImageGenerationRequested>();
            options.AddEventBridge<ImageGenerationCancelled>();
            options.AddEventBridge<VideoGenerationRequested>();
            options.AddEventBridge<VideoGenerationCancelled>();
            options.AddEventBridge<VideoProgressCheckRequested>();
            options.AddEventBridge<IndeterminateMediaTaskRetryRequested>();
            options.AddEventBridge<ImageGenerationProgress>();
            options.AddEventBridge<ImageGenerationCompleted>();
            options.AddEventBridge<ImageGenerationFailed>();
            options.AddEventBridge<VideoGenerationProgress>();
            options.AddEventBridge<VideoGenerationCompleted>();
            options.AddEventBridge<VideoGenerationFailed>();
        }
    }
}
