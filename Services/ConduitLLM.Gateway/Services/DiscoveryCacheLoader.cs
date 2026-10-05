using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Serialization;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Gateway.Services;

/// <summary>The endpoint and warmer cache exactly the same wire projection.</summary>
internal static class DiscoveryCacheLoader
{
    internal static async Task<DiscoveryModelsResult> LoadAsync(IDbContextFactory<ConduitDbContext> factory,
        string? capability, bool exposePricing, JsonSerializerOptions wireOptions, ILogger logger, CancellationToken token,
        TimeProvider? clock = null)
    {
        using var context = await factory.CreateDbContextAsync(token);
        var models = await DiscoveryModelProjector.ProjectAsync(context, capability, exposePricing, logger, token, clock);
        return new DiscoveryModelsResult
        {
            Data = models.Select(model => JsonSerializer.SerializeToElement(model,
                GatewayJsonTypeInfo.Require<DiscoveredModelDto>(wireOptions))).ToList(),
            Count = models.Count,
            CapabilityFilter = capability,
            PricingRefreshAt = models.Select(model => model.PricingRefreshAt).Min()
        };
    }
}
