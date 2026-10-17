using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Utilities;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

/// <summary>Cached request-time function pricing over the scalar persistence store.</summary>
public sealed class StoreBackedFunctionCostService : IFunctionCostService
{
    private readonly IFunctionPricingRuntimeStore _store;
    private readonly FunctionCostCache _cache;

    public StoreBackedFunctionCostService(
        IFunctionPricingRuntimeStore store,
        IMemoryCache memoryCache,
        ILogger<StoreBackedFunctionCostService> logger,
        IDistributedCache? distributedCache = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(logger);
        _cache = new FunctionCostCache(memoryCache, distributedCache, logger);
    }

    public async Task<FunctionCost?> GetCostForConfigurationAsync(
        int functionConfigurationId, CancellationToken cancellationToken = default)
    {
        if (functionConfigurationId <= 0)
            throw new ArgumentException("Function configuration ID must be greater than zero", nameof(functionConfigurationId));
        var key = $"Config:{functionConfigurationId}";
        var cache = await _cache.CaptureAsync(cancellationToken);
        var cached = await cache.GetAsync(key, FunctionsJsonContext.Default.FunctionCost, cancellationToken);
        if (cached is not null) return cached;
        var cost = await _store.FindEligibleCostAsync(functionConfigurationId, DateTime.UtcNow, cancellationToken);
        if (cost is not null)
            await cache.SetAsync(key, cost, FunctionsJsonContext.Default.FunctionCost, cancellationToken);
        return cost;
    }

    public Task ClearCacheAsync() => _cache.ClearAsync();

    public Task<FunctionCost?> GetCostByIdAsync(int costId, CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();
    public Task<List<FunctionCost>> ListCostsAsync(bool activeOnly = false, CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();
    public Task<int> CreateCostAsync(FunctionCost cost, CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();
    public Task UpdateCostAsync(FunctionCost cost, CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();
    public Task DeleteCostAsync(int costId, CancellationToken cancellationToken = default) =>
        throw ManagementNotSupported();

    private static NotSupportedException ManagementNotSupported() => new(
        "The native Gateway function-cost service supports configuration pricing; use Admin for cost management.");
}
