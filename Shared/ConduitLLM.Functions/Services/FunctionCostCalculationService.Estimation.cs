using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Models.Pricing;
using ConduitLLM.Functions.Utilities;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Cost estimation logic for FunctionCostCalculationService.
/// </summary>
public partial class FunctionCostCalculationService
{
    /// <inheritdoc />
    /// <remarks>
    /// Cost estimation is conservative (slightly over-estimates) to ensure sufficient balance
    /// is reserved before execution. The difference between estimated and actual cost is refunded
    /// after execution completes.
    ///
    /// Estimation strategies by pricing model:
    /// - FlatRate: Use CostPerExecution directly (exact)
    /// - PerResult: Estimate based on requested result count
    /// - PerToken: Estimate based on historical averages or max expected tokens
    /// - TimeBased: Estimate based on typical execution duration
    /// - Tiered: Use highest tier rate for conservatism
    /// - Hybrid: Conservative estimate for all dimensions (e.g., assume neural + all extractions for Exa)
    /// </remarks>
    public async Task<decimal> EstimateCostAsync(
        int functionConfigurationId,
        Dictionary<string, object> requestParameters,
        CancellationToken cancellationToken = default)
    {
        if (functionConfigurationId <= 0)
        {
            _logger.LogWarning("Function configuration ID is invalid: {ConfigId}. Cannot estimate cost.", functionConfigurationId);
            return 0m;
        }

        if (requestParameters == null || !requestParameters.Any())
        {
            _logger.LogWarning("Request parameters are null/empty for function configuration {ConfigId}. Cannot estimate cost.", functionConfigurationId);
            return 0m;
        }

        var functionCost = await _functionCostService.GetCostForConfigurationAsync(functionConfigurationId, cancellationToken);

        if (functionCost == null)
        {
            throw new InvalidOperationException(
                $"Cost information is required for function configuration {functionConfigurationId}.");
        }

        decimal estimatedCost = 0m;

        // Estimate based on pricing model
        switch (functionCost.PricingModel)
        {
            case Enums.FunctionPricingModel.FlatRate:
                estimatedCost = functionCost.CostPerExecution ?? 0m;
                break;

            case Enums.FunctionPricingModel.PerResult:
                estimatedCost = EstimatePerResultCost(functionCost, requestParameters);
                break;

            case Enums.FunctionPricingModel.PerToken:
                estimatedCost = EstimatePerTokenCost(functionCost, requestParameters);
                break;

            case Enums.FunctionPricingModel.TimeBased:
                estimatedCost = EstimateTimeBasedCost(functionCost, requestParameters);
                break;

            case Enums.FunctionPricingModel.Tiered:
                estimatedCost = EstimateTieredCost(functionCost, requestParameters);
                break;

            case Enums.FunctionPricingModel.Hybrid:
                estimatedCost = EstimateHybridCost(functionCost, requestParameters);
                break;

            default:
                _logger.LogWarning("Unknown pricing model {PricingModel} for function configuration {ConfigId}. Using flat rate fallback.",
                    functionCost.PricingModel, functionConfigurationId);
                estimatedCost = functionCost.CostPerExecution ?? 0m;
                break;
        }

        _logger.LogDebug("Estimated cost for function configuration {ConfigId} using pricing model {PricingModel} is {EstimatedCost}",
            functionConfigurationId, functionCost.PricingModel, estimatedCost);

        return estimatedCost;
    }

    /// <summary>
    /// Estimates cost for per-result pricing model.
    /// </summary>
    private decimal EstimatePerResultCost(Entities.FunctionCost functionCost, Dictionary<string, object> requestParameters)
    {
        if (!functionCost.CostPerResult.HasValue)
        {
            return 0m;
        }

        // Try to extract requested result count from parameters
        int requestedResults = 10; // Default assumption

        if (requestParameters.TryGetValue("numResults", out var numResultsObj))
        {
            requestedResults = JsonElementConverter.ConvertToInt32(numResultsObj) ?? requestedResults;
        }
        else if (requestParameters.TryGetValue("num_results", out var numResultsSnake))
        {
            requestedResults = JsonElementConverter.ConvertToInt32(numResultsSnake) ?? requestedResults;
        }
        else if (requestParameters.TryGetValue("limit", out var limitObj))
        {
            requestedResults = JsonElementConverter.ConvertToInt32(limitObj) ?? requestedResults;
        }

        var estimate = requestedResults * functionCost.CostPerResult.Value;

        _logger.LogDebug("Estimated per-result cost: {RequestedResults} results × ${CostPerResult} = ${Estimate}",
            requestedResults, functionCost.CostPerResult.Value, estimate);

        return estimate;
    }

    /// <summary>
    /// Estimates cost for per-token pricing model.
    /// </summary>
    private decimal EstimatePerTokenCost(Entities.FunctionCost functionCost, Dictionary<string, object> requestParameters)
    {
        if (!functionCost.CostPerToken.HasValue)
        {
            return 0m;
        }

        // Conservative estimate: assume 1000 tokens for Answer functions
        // Can be refined based on query length or historical averages
        int estimatedTokens = 1000;

        var estimate = estimatedTokens * functionCost.CostPerToken.Value;

        _logger.LogDebug("Estimated per-token cost: {EstimatedTokens} tokens × ${CostPerToken} = ${Estimate}",
            estimatedTokens, functionCost.CostPerToken.Value, estimate);

        return estimate;
    }

    /// <summary>
    /// Estimates cost for time-based pricing model.
    /// </summary>
    private decimal EstimateTimeBasedCost(Entities.FunctionCost functionCost, Dictionary<string, object> requestParameters)
    {
        if (!functionCost.CostPerMinute.HasValue)
        {
            return 0m;
        }

        // Conservative estimate: assume 1 minute execution time
        // Can be refined based on function type or historical averages
        decimal estimatedMinutes = 1m;

        var estimate = estimatedMinutes * functionCost.CostPerMinute.Value;

        _logger.LogDebug("Estimated time-based cost: {EstimatedMinutes} minutes × ${CostPerMinute} = ${Estimate}",
            estimatedMinutes, functionCost.CostPerMinute.Value, estimate);

        return estimate;
    }

    /// <summary>
    /// Estimates cost for tiered pricing model.
    /// </summary>
    private decimal EstimateTieredCost(Entities.FunctionCost functionCost, Dictionary<string, object> requestParameters)
    {
        if (string.IsNullOrWhiteSpace(functionCost.TieredPricing))
        {
            return 0m;
        }

        TieredPricingConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(
                functionCost.TieredPricing,
                FunctionsJsonContext.Default.TieredPricingConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse tiered pricing configuration for estimation");
            return 0m;
        }

        if (config == null || config.Tiers == null || !config.Tiers.Any())
        {
            return 0m;
        }

        // Extract requested count
        int requestedUnits = 10; // Default
        if (requestParameters.TryGetValue("numResults", out var numObj))
        {
            requestedUnits = JsonElementConverter.ConvertToInt32(numObj) ?? requestedUnits;
        }

        // Use the highest tier rate as conservative estimate
        var highestRate = config.Tiers.Max(t => t.CostPerUnit);
        var estimate = requestedUnits * highestRate;

        _logger.LogDebug("Estimated tiered cost (conservative): {RequestedUnits} units × ${HighestRate} = ${Estimate}",
            requestedUnits, highestRate, estimate);

        return estimate;
    }

    /// <summary>
    /// Estimates cost for hybrid pricing model.
    /// </summary>
    private decimal EstimateHybridCost(Entities.FunctionCost functionCost, Dictionary<string, object> requestParameters)
    {
        if (string.IsNullOrWhiteSpace(functionCost.PricingConfiguration))
        {
            throw new InvalidOperationException(
                $"Hybrid pricing configuration is required for cost '{functionCost.CostName}'.");
        }

        try
        {
            return functionCost.ProviderType switch
            {
                Enums.FunctionProviderType.Exa => EstimateExaHybridCost(
                    DeserializeHybridConfig(
                        functionCost,
                        FunctionsJsonContext.Default.ExaHybridPricingConfig), requestParameters),
                Enums.FunctionProviderType.Tavily => EstimateTavilySearchCost(
                    DeserializeHybridConfig(
                        functionCost,
                        FunctionsJsonContext.Default.TavilySearchPricingConfig), requestParameters),
                Enums.FunctionProviderType.Perplexity => EstimatePerplexityHybridCost(
                    DeserializeHybridConfig(
                        functionCost,
                        FunctionsJsonContext.Default.PerplexityHybridPricingConfig), requestParameters),
                _ => throw new InvalidOperationException(
                    $"Hybrid pricing is not supported for provider {functionCost.ProviderType} on cost '{functionCost.CostName}'.")
            };
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception,
                "Invalid {ProviderType} hybrid pricing configuration for cost {CostName} during estimation.",
                functionCost.ProviderType, functionCost.CostName);
            throw new InvalidOperationException(
                $"Invalid {functionCost.ProviderType} hybrid pricing configuration for cost '{functionCost.CostName}'.",
                exception);
        }
    }

    private decimal EstimateTavilySearchCost(
        TavilySearchPricingConfig config,
        Dictionary<string, object> requestParameters)
    {
        var hasDepth = requestParameters.TryGetValue("searchDepth", out var depth)
            || requestParameters.TryGetValue("search_depth", out depth);
        var advanced = hasDepth
            && string.Equals(JsonElementConverter.ConvertToString(depth), "advanced", StringComparison.OrdinalIgnoreCase);
        var credits = advanced ? config.AdvancedSearchCredits : config.BasicSearchCredits;

        var hasAutoParameters = requestParameters.TryGetValue("autoParameters", out var autoParameters)
            || requestParameters.TryGetValue("auto_parameters", out autoParameters);
        if (hasAutoParameters && JsonElementConverter.ConvertToBoolean(autoParameters) == true)
        {
            credits += config.AutoParametersCredits ?? 0;
        }

        return credits * config.CostPerCredit;
    }

    private static decimal EstimatePerplexityHybridCost(
        PerplexityHybridPricingConfig config,
        Dictionary<string, object> requestParameters)
    {
        var maxOutputTokens = requestParameters.TryGetValue("maxTokens", out var maxTokens)
            ? JsonElementConverter.ConvertToInt32(maxTokens) ?? 0
            : requestParameters.TryGetValue("max_tokens", out var maxTokensSnake)
                ? JsonElementConverter.ConvertToInt32(maxTokensSnake) ?? 0
                : 0;

        // The request does not expose a reliable tokenizer here. The base charge is always
        // reserved, and any declared maximum output is reserved at the output-token rate.
        return config.BaseRequestCost
            + maxOutputTokens * config.OutputTokenCostPerMillion / 1_000_000m;
    }

    /// <summary>
    /// Estimates cost for Exa.ai hybrid pricing.
    /// </summary>
    /// <remarks>
    /// Conservative estimation strategy:
    /// 1. Assume neural search (more expensive than keyword)
    /// 2. Check requested result count to determine tier
    /// 3. Assume ALL requested content extractions will succeed
    ///
    /// This ensures we reserve sufficient balance upfront.
    /// </remarks>
    private decimal EstimateExaHybridCost(ExaHybridPricingConfig config, Dictionary<string, object> requestParameters)
    {
        decimal totalEstimate = 0m;

        // Extract requested result count
        int requestedResults = 10; // Default
        if (requestParameters.TryGetValue("numResults", out var numObj))
        {
            requestedResults = JsonElementConverter.ConvertToInt32(numObj) ?? requestedResults;
        }
        else if (requestParameters.TryGetValue("num_results", out var numSnake))
        {
            requestedResults = JsonElementConverter.ConvertToInt32(numSnake) ?? requestedResults;
        }

        // 1. Estimate search cost (assume neural for conservatism)
        decimal searchEstimate;
        if (requestedResults <= (config.SearchCosts?.Neural?.Tier1.MaxResults ?? 25))
        {
            searchEstimate = config.SearchCosts?.Neural?.Tier1.Cost ?? 0m;
        }
        else
        {
            searchEstimate = config.SearchCosts?.Neural?.Tier2.Cost ?? 0m;
        }
        totalEstimate += searchEstimate;

        // 2. Estimate content extraction costs (assume all requested extractions succeed)
        bool textRequested = requestParameters.ContainsKey("text");
        bool highlightsRequested = requestParameters.ContainsKey("highlights");
        bool summaryRequested = requestParameters.ContainsKey("summary");

        if (textRequested)
        {
            var textEstimate = requestedResults * config.ContentExtractionCosts.Text;
            totalEstimate += textEstimate;
            _logger.LogDebug("Estimated text extraction: {Pages} pages × ${Cost} = ${Estimate}",
                requestedResults, config.ContentExtractionCosts.Text, textEstimate);
        }

        if (highlightsRequested)
        {
            var highlightsEstimate = requestedResults * config.ContentExtractionCosts.Highlights;
            totalEstimate += highlightsEstimate;
            _logger.LogDebug("Estimated highlights extraction: {Pages} pages × ${Cost} = ${Estimate}",
                requestedResults, config.ContentExtractionCosts.Highlights, highlightsEstimate);
        }

        if (summaryRequested)
        {
            var summaryEstimate = requestedResults * config.ContentExtractionCosts.Summary;
            totalEstimate += summaryEstimate;
            _logger.LogDebug("Estimated summary generation: {Pages} pages × ${Cost} = ${Estimate}",
                requestedResults, config.ContentExtractionCosts.Summary, summaryEstimate);
        }

        _logger.LogDebug("Estimated Exa hybrid cost (conservative): Search=${SearchEstimate}, Total=${TotalEstimate}",
            searchEstimate, totalEstimate);

        return totalEstimate;
    }
}
