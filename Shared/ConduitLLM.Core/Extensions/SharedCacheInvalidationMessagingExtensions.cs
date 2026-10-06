using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Consumers;
using ConduitLLM.Core.Events;

using Microsoft.Extensions.DependencyInjection;

using Wolverine;

namespace ConduitLLM.Core.Extensions
{
    /// <summary>
    /// Registration for the cache-invalidation handlers that run in BOTH the Gateway and
    /// Admin hosts (defined in <see cref="ConduitLLM.Core.Consumers"/>), migrated to
    /// <c>IEventHandler&lt;T&gt;</c> in epic #909 (issue #919). Lives in Core because both
    /// hosts reference Core (Admin does not reference Gateway).
    /// </summary>
    public static class SharedCacheInvalidationMessagingExtensions
    {
        /// <summary>Registers the shared Core cache-invalidation handlers.</summary>
        public static IServiceCollection AddSharedCacheInvalidationHandlers(this IServiceCollection services)
        {
            services.AddEventHandler<GlobalSettingChanged, GlobalSettingCacheInvalidationHandler>();
            services.AddEventHandler<GlobalSettingsReloadRequested, GlobalSettingsReloadHandler>();
            services.AddEventHandler<FunctionConfigurationChanged, FunctionConfigurationCacheInvalidationHandler>();
            services.AddEventHandler<FunctionDiscoveryCacheInvalidationRequested, FunctionDiscoveryCacheInvalidationRequestHandler>();
            return services;
        }

        /// <summary>
        /// The shared Core cache event types bridged to handlers. One list drives both
        /// backends' bridge registration so they cannot drift (epic #909 Phase 2, #925).
        /// </summary>
        public static readonly IReadOnlyList<Type> BridgedEventTypes = new[]
        {
            typeof(GlobalSettingChanged),
            typeof(GlobalSettingsReloadRequested),
            typeof(FunctionConfigurationChanged),
            typeof(FunctionDiscoveryCacheInvalidationRequested),
        };

        /// <summary>Registers the Wolverine bridge handlers for the shared Core cache events (#925).</summary>
        public static void AddSharedCacheInvalidationBridges(this WolverineOptions options)
        {
            options.AddEventBridge<GlobalSettingChanged>();
            options.AddEventBridge<GlobalSettingsReloadRequested>();
            options.AddEventBridge<FunctionConfigurationChanged>();
            options.AddEventBridge<FunctionDiscoveryCacheInvalidationRequested>();
        }
    }
}
