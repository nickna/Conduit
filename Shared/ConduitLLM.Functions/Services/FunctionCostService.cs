using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Utilities;
using ConduitLLM.Functions.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Service for managing and retrieving function costs, with hybrid caching support (L1: Memory, L2: Redis).
/// </summary>
/// <remarks>
/// This service follows the same architecture as ModelCostService for LLM operations.
/// It provides cached access to function cost configurations and handles priority-based selection.
///
/// Caching Strategy:
/// - L1 Cache: In-process Memory (15 minute TTL)
/// - L2 Cache: Redis Distributed (1 hour TTL)
/// - Cascade: L1 → L2 → Database
/// </remarks>
public class FunctionCostService : IFunctionCostService
{
    private readonly IFunctionCostRepository _functionCostRepository;
    private readonly IFunctionCostMappingRepository _functionCostMappingRepository;
    private readonly HybridCacheAccessor _cache;
    private readonly ILogger<FunctionCostService> _logger;
    private const string AllCostsCacheKey = "All";

    /// <summary>
    /// Creates a new instance of the FunctionCostService.
    /// </summary>
    /// <param name="functionCostRepository">The function cost repository.</param>
    /// <param name="functionCostMappingRepository">The function cost mapping repository.</param>
    /// <param name="memoryCache">The memory cache.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="distributedCache">The distributed cache (optional, Redis).</param>
    public FunctionCostService(
        IFunctionCostRepository functionCostRepository,
        IFunctionCostMappingRepository functionCostMappingRepository,
        IMemoryCache memoryCache,
        ILogger<FunctionCostService> logger,
        IDistributedCache? distributedCache = null)
    {
        _functionCostRepository = functionCostRepository ?? throw new ArgumentNullException(nameof(functionCostRepository));
        _functionCostMappingRepository = functionCostMappingRepository ?? throw new ArgumentNullException(nameof(functionCostMappingRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = new HybridCacheAccessor(
            memoryCache,
            distributedCache,
            logger,
            "FunctionCost:",
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1));
    }

    /// <inheritdoc />
    public async Task<FunctionCost?> GetCostForConfigurationAsync(
        int functionConfigurationId,
        CancellationToken cancellationToken = default)
    {
        if (functionConfigurationId <= 0)
        {
            throw new ArgumentException("Function configuration ID must be greater than zero", nameof(functionConfigurationId));
        }

        string cacheKey = $"Config:{functionConfigurationId}";

        // Try hybrid cache first
        var cachedCost = await _cache.GetAsync(
            cacheKey,
            FunctionsJsonContext.Default.FunctionCost,
            cancellationToken);
        if (cachedCost != null)
        {
            _logger.LogDebug("Cache hit for function cost: ConfigId={ConfigId}", functionConfigurationId);
            return cachedCost;
        }

        _logger.LogDebug("Cache miss for function cost: ConfigId={ConfigId}, querying database", functionConfigurationId);

        // Get active cost mappings for this configuration
        var mappings = await _functionCostMappingRepository.GetByFunctionConfigurationIdAsync(functionConfigurationId, cancellationToken);

        if (mappings == null || !mappings.Any())
        {
            _logger.LogDebug("No cost mappings found for function configuration: {ConfigId}", functionConfigurationId);
            return null;
        }

        // Load full cost objects for active mappings
        var costIds = mappings.Where(m => m.IsActive).Select(m => m.FunctionCostId).Distinct();
        var costs = new List<FunctionCost>();

        foreach (var costId in costIds)
        {
            var cost = await _functionCostRepository.GetByIdAsync(costId, cancellationToken);
            if (cost != null)
            {
                costs.Add(cost);
            }
        }

        // Filter by active status and effective date range, then select highest priority
        var now = DateTime.UtcNow;
        var functionCost = costs
            .Where(cost => cost.IsActive && cost.EffectiveDate <= now)
            .Where(cost => !cost.ExpiryDate.HasValue || cost.ExpiryDate.Value > now)
            .OrderByDescending(cost => cost.Priority)
            .ThenByDescending(cost => cost.EffectiveDate)
            .FirstOrDefault();

        if (functionCost == null)
        {
            _logger.LogDebug("No active function cost found for configuration: {ConfigId}", functionConfigurationId);
        }
        else
        {
            _logger.LogDebug("Found function cost: {CostName} (Priority={Priority}) for configuration: {ConfigId}",
                functionCost.CostName, functionCost.Priority, functionConfigurationId);
        }

        if (functionCost is not null)
        {
            await _cache.SetAsync(
                cacheKey,
                functionCost,
                FunctionsJsonContext.Default.FunctionCost,
                cancellationToken);
        }
        return functionCost;
    }

    /// <inheritdoc />
    public async Task<FunctionCost?> GetCostByIdAsync(int costId, CancellationToken cancellationToken = default)
    {
        if (costId <= 0)
        {
            throw new ArgumentException("Cost ID must be greater than zero", nameof(costId));
        }

        string cacheKey = $"Id:{costId}";

        // Try hybrid cache first
        var cachedCost = await _cache.GetAsync(
            cacheKey,
            FunctionsJsonContext.Default.FunctionCost,
            cancellationToken);
        if (cachedCost != null)
        {
            _logger.LogDebug("Cache hit for function cost ID: {CostId}", costId);
            return cachedCost;
        }

        var cost = await _functionCostRepository.GetByIdAsync(costId, cancellationToken);

        if (cost != null)
        {
            await _cache.SetAsync(
                cacheKey,
                cost,
                FunctionsJsonContext.Default.FunctionCost,
                cancellationToken);
        }

        return cost;
    }

    /// <inheritdoc />
    public async Task<List<FunctionCost>> ListCostsAsync(bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        string cacheKey = activeOnly ? $"{AllCostsCacheKey}_Active" : AllCostsCacheKey;

        // Try hybrid cache first
        var cachedCosts = await _cache.GetAsync(
            cacheKey,
            FunctionsJsonContext.Default.ListFunctionCost,
            cancellationToken);
        if (cachedCosts != null)
        {
            _logger.LogDebug("Cache hit for function costs list (activeOnly={ActiveOnly})", activeOnly);
            return cachedCosts;
        }

        var costs = await _functionCostRepository.GetAllAsync(cancellationToken);

        if (activeOnly)
        {
            var now = DateTime.UtcNow;
            costs = costs
                .Where(c => c.IsActive && c.EffectiveDate <= now)
                .Where(c => !c.ExpiryDate.HasValue || c.ExpiryDate.Value > now)
                .ToList();
        }

        await _cache.SetAsync(
            cacheKey,
            costs,
            FunctionsJsonContext.Default.ListFunctionCost,
            cancellationToken);
        return costs;
    }

    /// <inheritdoc />
    public async Task<int> CreateCostAsync(FunctionCost cost, CancellationToken cancellationToken = default)
    {
        if (cost == null)
        {
            throw new ArgumentNullException(nameof(cost));
        }

        cost.CreatedAt = DateTime.UtcNow;
        cost.UpdatedAt = DateTime.UtcNow;

        var costId = await _functionCostRepository.CreateAsync(cost, cancellationToken);

        await InvalidateCacheAsync(costId, []);

        _logger.LogInformation("Created function cost: {CostName} (ID={CostId})", cost.CostName, costId);
        return costId;
    }

    /// <inheritdoc />
    public async Task UpdateCostAsync(FunctionCost cost, CancellationToken cancellationToken = default)
    {
        if (cost == null)
        {
            throw new ArgumentNullException(nameof(cost));
        }

        var affectedConfigurationIds = await GetMappedConfigurationIdsAsync(cost.Id, cancellationToken);
        cost.UpdatedAt = DateTime.UtcNow;

        await _functionCostRepository.UpdateAsync(cost, cancellationToken);

        await InvalidateCacheAsync(cost.Id, affectedConfigurationIds);

        _logger.LogInformation("Updated function cost: {CostName} (ID={CostId})", cost.CostName, cost.Id);
    }

    /// <inheritdoc />
    public async Task DeleteCostAsync(int costId, CancellationToken cancellationToken = default)
    {
        if (costId <= 0)
        {
            throw new ArgumentException("Cost ID must be greater than zero", nameof(costId));
        }

        var affectedConfigurationIds = await GetMappedConfigurationIdsAsync(costId, cancellationToken);
        await _functionCostRepository.DeleteAsync(costId, cancellationToken);

        await InvalidateCacheAsync(costId, affectedConfigurationIds);

        _logger.LogInformation("Deleted function cost: ID={CostId}", costId);
    }

    /// <inheritdoc />
    public async Task ClearCacheAsync()
    {
        await InvalidateCacheAsync(null, []);
    }

    private async Task<List<int>> GetMappedConfigurationIdsAsync(
        int costId,
        CancellationToken cancellationToken)
    {
        var existingCost = await _functionCostRepository.GetByIdAsync(costId, cancellationToken);
        return existingCost?.FunctionMappings
            .Select(mapping => mapping.FunctionConfigurationId)
            .Distinct()
            .ToList() ?? [];
    }

    private async Task InvalidateCacheAsync(int? costId, IEnumerable<int> functionConfigurationIds)
    {
        var keysToRemove = new HashSet<string>
        {
            AllCostsCacheKey,
            $"{AllCostsCacheKey}_Active"
        };

        if (costId.HasValue)
        {
            keysToRemove.Add($"Id:{costId.Value}");
        }

        foreach (var functionConfigurationId in functionConfigurationIds)
        {
            keysToRemove.Add($"Config:{functionConfigurationId}");
        }

        await _cache.RemoveAsync(keysToRemove);

        _logger.LogInformation(
            "Cleared {CacheKeyCount} function cost cache entries for CostId={CostId}",
            keysToRemove.Count,
            costId);
    }

}
