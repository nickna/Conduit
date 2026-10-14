using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Consumers
{
    /// <summary>
    /// Consumer that handles GlobalSettingChanged events to invalidate cached settings
    /// across all instances in a distributed deployment.
    ///
    /// This ensures cache consistency when settings are modified via the Admin API.
    /// Both Gateway API and Admin API register this consumer to keep their caches synchronized.
    /// </summary>
    public class GlobalSettingCacheInvalidationHandler : IEventHandler<GlobalSettingChanged>
    {
        private readonly IGlobalSettingsCacheService _cacheService;
        private readonly ILogger<GlobalSettingCacheInvalidationHandler> _logger;
        private readonly IFunctionDiscoveryCacheService? _functions;

        public GlobalSettingCacheInvalidationHandler(
            IGlobalSettingsCacheService cacheService,
            ILogger<GlobalSettingCacheInvalidationHandler> logger,
            IFunctionDiscoveryCacheService? functions = null)
        {
            _cacheService = cacheService;
            _logger = logger;
            _functions = functions;
        }

        public async Task HandleAsync(GlobalSettingChanged message, IEventContext context)
        {
            _logger.LogInformation(
                "Received GlobalSettingChanged event for setting '{SettingKey}' (ID: {SettingId}, ChangeType: {ChangeType})",
                message.SettingKey,
                message.SettingId,
                message.ChangeType);

            // Instances compete for the service queue, so the receiving instance must
            // broadcast to every process-local cache. Reuse the event ID so delayed
            // copies delivered to another service do not reload the same change again.
            await _cacheService.PublishReloadAsync(message.EventId);
            if (message.SettingKey == "Functions.DiscoveryCacheEnabled" && _functions is not null)
                await _functions.InvalidateAllFunctionDiscoveryAsync(context.CancellationToken);

            _logger.LogInformation(
                "Published cluster cache reload for setting '{SettingKey}'",
                message.SettingKey);
        }
    }
}
