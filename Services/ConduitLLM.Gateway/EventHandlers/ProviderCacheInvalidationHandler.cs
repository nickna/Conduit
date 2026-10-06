using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Interfaces;

namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles Provider events to refresh in-memory settings and invalidate discovery cache.
    /// Critical for maintaining runtime configuration consistency.
    /// </summary>
    public class ProviderCacheInvalidationHandler :
        IEventHandler<ProviderCreated>,
        IEventHandler<ProviderUpdated>,
        IEventHandler<ProviderDeleted>
    {
        private readonly ISettingsRefreshService _settingsRefreshService;
        private readonly IDiscoveryCacheService _discoveryCacheService;
        private readonly ILogger<ProviderCacheInvalidationHandler> _logger;
        private readonly IModelMappingCacheInvalidator? _mappings;

        public ProviderCacheInvalidationHandler(
            ISettingsRefreshService settingsRefreshService,
            IDiscoveryCacheService discoveryCacheService,
            ILogger<ProviderCacheInvalidationHandler> logger, IModelMappingCacheInvalidator? mappings = null)
        {
            _settingsRefreshService = settingsRefreshService ?? throw new ArgumentNullException(nameof(settingsRefreshService));
            _discoveryCacheService = discoveryCacheService ?? throw new ArgumentNullException(nameof(discoveryCacheService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mappings = mappings;
        }

        public async Task HandleAsync(ProviderCreated message, IEventContext context)
        {
            if (_mappings is not null) await _mappings.InvalidateAsync(context.CancellationToken);
            await RefreshAndInvalidateAsync(message.ProviderId, "creation",
                invalidateDiscovery: true);
        }

        public async Task HandleAsync(ProviderUpdated message, IEventContext context)
        {
            if (_mappings is not null) await _mappings.InvalidateAsync(context.CancellationToken);
            var invalidateDiscovery = message.ChangedProperties.Contains("IsEnabled") ||
                                     message.ChangedProperties.Contains("IsActive");
            await RefreshAndInvalidateAsync(message.ProviderId, "update",
                invalidateDiscovery: invalidateDiscovery);
        }

        public async Task HandleAsync(ProviderDeleted message, IEventContext context)
        {
            if (_mappings is not null) await _mappings.InvalidateAsync(context.CancellationToken);
            await RefreshAndInvalidateAsync(message.ProviderId, "deletion",
                invalidateDiscovery: true);
        }

        private async Task RefreshAndInvalidateAsync(int providerId, string operation, bool invalidateDiscovery)
        {
            _logger.LogInformation(
                "Processing provider {Operation} event: Provider ID {ProviderId}",
                operation, providerId);

            await _settingsRefreshService.RefreshProvidersAsync();

            if (invalidateDiscovery)
            {
                await _discoveryCacheService.InvalidateAllDiscoveryAsync();
            }

            _logger.LogInformation(
                "Successfully refreshed provider credentials after {Operation} of Provider ID {ProviderId}",
                operation, providerId);
        }
    }
}
