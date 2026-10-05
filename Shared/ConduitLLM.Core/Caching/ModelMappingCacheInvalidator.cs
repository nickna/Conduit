using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Caching;

public sealed class ModelMappingCacheInvalidator(
    [FromKeyedServices(ApplicationCacheOptions.ServiceKey)] IFusionCache cache,
    ApplicationCacheGeneration generation) : IModelMappingCacheInvalidator
{
    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        const ApplicationCacheDomain domain = ApplicationCacheDomain.Mappings;
        try
        {
            await generation.InvalidateAsync(domain, cancellationToken);
            await cache.RemoveByTagAsync(ApplicationCacheOptions.Tag(domain), token: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ApplicationCacheMetrics.InvalidationFailed(domain); throw new ApplicationCacheInvalidationException(domain, ex); }
    }
}

// Rollout-only adapter; removed with the legacy selection in FC-8.
internal sealed class LegacyMappingCacheInvalidator(ICacheManager cache) : IModelMappingCacheInvalidator
{
    public Task InvalidateAsync(CancellationToken cancellationToken = default) => cache.ClearRegionAsync(CacheRegion.ModelMetadata, cancellationToken);
}
