using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;

using ConfigurationModelCostService = ConduitLLM.Configuration.Interfaces.IModelCostService;


namespace ConduitLLM.Gateway.Consumers
{
    /// <summary>
    /// Handles ModelCostChanged events for cache invalidation.
    /// Invalidates the model cost cache, the pricing rules cache, and the discovery
    /// cache (discovery responses embed pricing, see #1238).
    /// </summary>
    public class ModelCostCacheInvalidationHandler : IEventHandler<ModelCostChanged>
    {
        private readonly ConfigurationModelCostService _modelCostService;
        private readonly ICachedPricingRulesService? _pricingRulesCache;
        private readonly IDiscoveryCacheService _discoveryCacheService;
        private readonly ILogger<ModelCostCacheInvalidationHandler> _logger;
        private readonly IModelMappingCacheInvalidator? _mappings;

        /// <summary>
        /// Initializes a new instance of the ModelCostCacheInvalidationHandler
        /// </summary>
        /// <param name="modelCostService">The model cost service used by the billing path</param>
        /// <param name="pricingRulesCache">Optional pricing rules cache</param>
        /// <param name="discoveryCacheService">Discovery cache holding pricing-bearing model payloads</param>
        /// <param name="logger">Logger for diagnostics</param>
        /// <param name="mappings">Routing cache holding attached cost snapshots</param>
        public ModelCostCacheInvalidationHandler(
            ConfigurationModelCostService modelCostService,
            ICachedPricingRulesService? pricingRulesCache,
            IDiscoveryCacheService discoveryCacheService,
            ILogger<ModelCostCacheInvalidationHandler> logger, IModelMappingCacheInvalidator? mappings = null)
        {
            _modelCostService = modelCostService ?? throw new ArgumentNullException(nameof(modelCostService));
            _pricingRulesCache = pricingRulesCache;
            _discoveryCacheService = discoveryCacheService ?? throw new ArgumentNullException(nameof(discoveryCacheService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mappings = mappings;
        }

        /// <summary>
        /// Consumes ModelCostChanged events and logs them for monitoring
        /// </summary>
        /// <param name="message">The model cost change event</param>
        /// <param name="context">The consume context containing the event</param>
        public async Task HandleAsync(ModelCostChanged message, IEventContext context)
        {
            var @event = message;

            _logger.LogInformation(
                "ModelCostChanged event received - ModelCostId: {ModelCostId}, CostName: {CostName}, ChangeType: {ChangeType}",
                @event.ModelCostId,
                @event.CostName,
                @event.ChangeType);

            if (@event.ChangedProperties?.Length > 0)
            {
                _logger.LogDebug(
                    "Model cost properties changed: {ChangedProperties}",
                    string.Join(", ", @event.ChangedProperties));
            }

            // Log warning for cost changes that might affect billing
            if (@event.ChangeType == "Updated" &&
                (@event.ChangedProperties?.Contains("InputCost") == true ||
                 @event.ChangedProperties?.Contains("OutputCost") == true ||
                 @event.ChangedProperties?.Contains("Cost") == true))
            {
                _logger.LogWarning(
                    "Model pricing changed for cost '{CostName}'. This will affect cost calculations for new requests.",
                    @event.CostName);
            }

            // Clear the cache used by CostCalculationService. ModelCostChanged only contains
            // the database cost ID, while lookups are also cached by provider model identifier,
            // so the whole region must be invalidated to cover every affected mapping.
            await _modelCostService.ClearCacheAsync(context.CancellationToken);
            if (_mappings is not null) await _mappings.InvalidateAsync(context.CancellationToken);
            _logger.LogInformation("Billing model cost cache invalidated for ModelCostId: {ModelCostId}", @event.ModelCostId);

            // Discovery responses embed pricing from ModelCost, so a repricing must also
            // drop cached discovery payloads or clients keep seeing the old rates until TTL.
            await _discoveryCacheService.InvalidateAllDiscoveryAsync(context.CancellationToken);
            _logger.LogInformation("Discovery cache invalidated for ModelCostId: {ModelCostId}", @event.ModelCostId);

            // Invalidate pricing rules cache if available
            if (_pricingRulesCache != null && @event.ModelCostId > 0)
            {
                await _pricingRulesCache.InvalidateCacheAsync(@event.ModelCostId, context.CancellationToken);
                _logger.LogInformation(
                    "Pricing rules cache invalidated for ModelCostId: {ModelCostId}",
                    @event.ModelCostId);
            }
        }
    }
}
