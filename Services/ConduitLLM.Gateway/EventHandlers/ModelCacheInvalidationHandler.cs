using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles ModelUpdated events to invalidate discovery cache
    /// Critical for ensuring updated model parameters are reflected in the discovery API
    /// </summary>
    public class ModelCacheInvalidationHandler : IEventHandler<ModelUpdated>
    {
        private readonly IDiscoveryCacheService _discoveryCacheService;
        private readonly IModelCapabilityService _modelCapabilityService;
        private readonly ILogger<ModelCacheInvalidationHandler> _logger;
        private readonly IModelMappingCacheInvalidator? _mappings;

        public ModelCacheInvalidationHandler(
            IDiscoveryCacheService discoveryCacheService,
            IModelCapabilityService modelCapabilityService,
            ILogger<ModelCacheInvalidationHandler> logger, IModelMappingCacheInvalidator? mappings = null)
        {
            _discoveryCacheService = discoveryCacheService ?? throw new ArgumentNullException(nameof(discoveryCacheService));
            _modelCapabilityService = modelCapabilityService ?? throw new ArgumentNullException(nameof(modelCapabilityService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mappings = mappings;
        }

        /// <summary>
        /// Handles ModelUpdated events by invalidating discovery cache
        /// </summary>
        public async Task HandleAsync(ModelUpdated message, IEventContext context)
        {
            _logger.LogInformation(
                "Processing ModelUpdated event: {ModelName} (ID: {ModelId}, ChangeType: {ChangeType}, ParametersChanged: {ParametersChanged})",
                message.ModelName,
                message.ModelId,
                message.ChangeType,
                message.ParametersChanged);

            if (_mappings is not null) await _mappings.InvalidateAsync(context.CancellationToken);
            await _discoveryCacheService.InvalidateAllDiscoveryAsync(context.CancellationToken);
            await _modelCapabilityService.RefreshCacheAsync();

            _logger.LogInformation(
                "Invalidated all discovery cache entries after {ChangeType} of model {ModelName} (ID: {ModelId})",
                message.ChangeType,
                message.ModelName,
                message.ModelId);

            if (message.ParametersChanged)
            {
                _logger.LogInformation(
                    "Model parameters were updated for {ModelName} - UI components will reflect new parameter definitions",
                    message.ModelName);
            }
        }
    }
}
