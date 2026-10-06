using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Gateway.Consumers;

/// <summary>Expires complete routing graphs and discovery for every mapping mutation, including alias renames.</summary>
public sealed class ModelMappingCacheInvalidationHandler : IEventHandler<ModelMappingChanged>
{
    private readonly IModelMappingCacheInvalidator _mappings;
    private readonly IDiscoveryCacheService _discovery;
    private readonly ILogger<ModelMappingCacheInvalidationHandler> _logger;
    public ModelMappingCacheInvalidationHandler(IModelMappingCacheInvalidator mappings,
        IDiscoveryCacheService discovery, ILogger<ModelMappingCacheInvalidationHandler> logger)
    {
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    public async Task HandleAsync(ModelMappingChanged message, IEventContext context)
    {
        await _mappings.InvalidateAsync(context.CancellationToken);
        await _discovery.InvalidateAllDiscoveryAsync(context.CancellationToken);
        _logger.LogInformation("Expired mapping and discovery dependencies after {ChangeType} of mapping {MappingId}",
            message.ChangeType, message.MappingId);
    }
}
