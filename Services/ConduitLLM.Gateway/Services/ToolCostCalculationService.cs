using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Serialization;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Indicates that provider tool usage cannot be priced reliably.
    /// </summary>
    public sealed class ToolCostCalculationException : InvalidOperationException
    {
        public ToolCostCalculationException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Result of a tool cost calculation, including cost and diagnostic information.
    /// </summary>
    public class ToolCostResult
    {
        /// <summary>
        /// Total calculated cost. -1 indicates a calculation failure.
        /// </summary>
        public decimal TotalCost { get; init; }

        /// <summary>
        /// Tool names that were used but had no cost configuration.
        /// Empty if all tools were configured.
        /// </summary>
        public List<string> UnconfiguredToolNames { get; init; } = new();

        /// <summary>
        /// True if the cost calculation encountered an error.
        /// </summary>
        public bool Failed => TotalCost < 0;

        /// <summary>
        /// True if some tools were used but had no cost configuration.
        /// </summary>
        public bool HasUnconfiguredTools => UnconfiguredToolNames.Count > 0;
    }

    /// <summary>
    /// Service for calculating costs of tool usage across different providers.
    /// </summary>
    public interface IToolCostCalculationService
    {
        /// <summary>
        /// Calculates the total cost for tool usage based on provider configuration.
        /// Returns a result containing the cost and any unconfigured tool names.
        /// </summary>
        /// <param name="toolUsage">Tool usage data extracted from provider response</param>
        /// <param name="providerType">The provider type to look up tool costs</param>
        /// <returns>Tool cost result with cost and diagnostic info</returns>
        Task<ToolCostResult> CalculateToolCostsAsync(ToolUsageData toolUsage, ProviderType providerType);

        /// <summary>
        /// Serializes tool usage data to JSON for storage in BillingAuditEvent.
        /// </summary>
        /// <param name="toolUsage">Tool usage data to serialize</param>
        /// <returns>JSON string representation of tool usage</returns>
        string SerializeToolUsage(ToolUsageData toolUsage);
    }

    /// <summary>
    /// Implementation of tool cost calculation service.
    /// Uses IProviderToolCache for high-performance lookups when available,
    /// falls back to direct database queries otherwise.
    /// </summary>
    public class ToolCostCalculationService : IToolCostCalculationService
    {
        private static readonly JsonSerializerOptions ToolUsageJsonOptions = CreateToolUsageJsonOptions();
        private readonly IDbContextFactory<ConduitDbContext> _contextFactory;
        private readonly IProviderToolCache? _cache;
        private readonly ILogger<ToolCostCalculationService> _logger;

        /// <summary>
        /// Initializes a new instance of the ToolCostCalculationService.
        /// </summary>
        public ToolCostCalculationService(
            IDbContextFactory<ConduitDbContext> contextFactory,
            ILogger<ToolCostCalculationService> logger,
            IProviderToolCache? cache = null)
        {
            _contextFactory = contextFactory;
            _logger = logger;
            _cache = cache;
        }

        /// <inheritdoc/>
        public async Task<ToolCostResult> CalculateToolCostsAsync(ToolUsageData toolUsage, ProviderType providerType)
        {
            if (toolUsage?.Tools == null || toolUsage.Tools.Count == 0)
                return new ToolCostResult { TotalCost = 0 };

            // Do not turn configuration-store failures into a zero cost. The caller must fail
            // billing (and alert) instead of delivering provider-funded tool usage for free.
            List<ProviderTool> providerTools;
            try
            {
                providerTools = await GetActiveToolsForProviderAsync(providerType);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unable to load tool pricing for provider {ProviderType}", providerType);
                throw new ToolCostCalculationException(
                    $"Unable to load tool pricing for provider {providerType}.", ex);
            }

            var totalCost = 0m;
            var unconfiguredTools = new List<string>();

            foreach (var toolUsageItem in toolUsage.Tools)
            {
                var providerTool = providerTools
                    .Find(pt => pt.ToolName == toolUsageItem.ToolName);

                if (providerTool?.CostPerUnit.HasValue == true)
                {
                    var usage = CalculateUsageAmount(toolUsageItem, providerTool.BillingUnit);
                    var cost = providerTool.CostPerUnit.Value * usage;

                    totalCost += cost;

                    _logger.LogDebug("Tool cost calculated: {ToolName} = {Usage} {BillingUnit} × ${CostPerUnit} = ${Cost}",
                        toolUsageItem.ToolName, usage, providerTool.BillingUnit, providerTool.CostPerUnit, cost);
                }
                else
                {
                    unconfiguredTools.Add(toolUsageItem.ToolName);
                }
            }

            if (unconfiguredTools.Count > 0)
            {
                var names = string.Join(", ", unconfiguredTools.Distinct(StringComparer.Ordinal));
                _logger.LogCritical(
                    "Refusing to calculate a partial tool cost for provider {ProviderType}; missing active cost configuration for: {ToolNames}",
                    providerType, names);
                throw new ToolCostCalculationException(
                    $"Active tool cost configuration is required for provider {providerType}: {names}.");
            }

            return new ToolCostResult { TotalCost = totalCost };
        }

        /// <inheritdoc/>
        public string SerializeToolUsage(ToolUsageData toolUsage)
        {
            try
            {
                if (toolUsage == null)
                {
                    return "{}";
                }

                return JsonSerializer.Serialize(
                    toolUsage,
                    GatewayJsonTypeInfo.Require<ToolUsageData>(ToolUsageJsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serialize tool usage data");
                return "{}";
            }
        }

        private static JsonSerializerOptions CreateToolUsageJsonOptions() => new()
        {
            TypeInfoResolver = GatewayInternalJsonContext.Default,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };

        /// <summary>
        /// Gets all active tools for a provider, using cache when available.
        /// </summary>
        private async Task<List<ProviderTool>> GetActiveToolsForProviderAsync(ProviderType providerType)
        {
            if (_cache != null)
            {
                return await _cache.GetActiveToolsForProviderAsync(
                    providerType,
                    LoadToolsFromDatabaseAsync);
            }

            return await LoadToolsFromDatabaseAsync(providerType);
        }

        /// <summary>
        /// Loads active tools from the database for a given provider.
        /// Used as the cache fallback function.
        /// </summary>
        private async Task<List<ProviderTool>> LoadToolsFromDatabaseAsync(ProviderType providerType)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.ProviderTools
                .Where(pt => pt.Provider == providerType && pt.IsActive)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <summary>
        /// Calculates the usage amount based on the tool's billing unit.
        /// Supports DurationSeconds from provider responses with automatic unit conversion.
        /// </summary>
        private static decimal CalculateUsageAmount(ToolUsageItem toolUsageItem, string? billingUnit)
        {
            var unit = billingUnit?.ToLowerInvariant();
            return unit switch
            {
                ProviderToolBillingUnits.Hours => GetDurationInHours(toolUsageItem),
                ProviderToolBillingUnits.Minutes => GetDurationInMinutes(toolUsageItem),
                ProviderToolBillingUnits.Requests => toolUsageItem.Count,
                ProviderToolBillingUnits.Searches => toolUsageItem.Count,
                ProviderToolBillingUnits.Executions => toolUsageItem.Count,
                ProviderToolBillingUnits.Characters => toolUsageItem.Count,
                ProviderToolBillingUnits.Tokens => toolUsageItem.Count,
                null or "" => toolUsageItem.Count,
                _ => toolUsageItem.Count // Validated at save time, but defensive fallback
            };
        }

        /// <summary>
        /// Gets duration in hours, converting from DurationSeconds if available.
        /// Falls back to Duration (already in hours), then to Count.
        /// </summary>
        private static decimal GetDurationInHours(ToolUsageItem item)
        {
            if (item.DurationSeconds.HasValue)
                return item.DurationSeconds.Value / 3600m;
            return item.Duration ?? item.Count;
        }

        /// <summary>
        /// Gets duration in minutes, converting from DurationSeconds if available.
        /// Falls back to Duration (already in minutes), then to Count.
        /// </summary>
        private static decimal GetDurationInMinutes(ToolUsageItem item)
        {
            if (item.DurationSeconds.HasValue)
                return item.DurationSeconds.Value / 60m;
            return item.Duration ?? item.Count;
        }
    }
}
