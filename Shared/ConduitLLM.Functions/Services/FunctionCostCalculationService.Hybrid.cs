using System.Text.Json;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Models.Pricing;
using ConduitLLM.Functions.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Hybrid pricing model calculations for FunctionCostCalculationService.
/// </summary>
public partial class FunctionCostCalculationService
{
    /// <summary>
    /// Calculates cost using hybrid pricing model (multiple pricing dimensions).
    /// </summary>
    /// <param name="functionCost">The function cost configuration.</param>
    /// <param name="usage">The usage data containing multiple billing dimensions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The calculated cost based on hybrid pricing rules.</returns>
    /// <remarks>
    /// Hybrid pricing combines multiple cost components:
    ///
    /// For Exa.ai example:
    /// 1. Search cost (based on type: neural vs keyword, and result count tier)
    /// 2. Content extraction costs (text, highlights, summary) per page
    ///
    /// Total Cost = SearchCost + ContentExtractionCosts
    ///
    /// Configuration is stored as JSON in PricingConfiguration field.
    /// The specific structure depends on the provider (e.g., ExaHybridPricingConfig for Exa).
    ///
    /// This model supports the most complex pricing scenarios.
    /// </remarks>
    private async Task<decimal> CalculateHybridCostAsync(
        FunctionCost functionCost,
        FunctionExecutionUsage usage,
        CancellationToken cancellationToken)
    {
        // Parse hybrid pricing configuration
        if (string.IsNullOrWhiteSpace(functionCost.PricingConfiguration))
        {
            _logger.LogError("Hybrid pricing model configured but PricingConfiguration JSON is null/empty for cost {CostName}.",
                functionCost.CostName);
            throw new InvalidOperationException(
                $"Hybrid pricing configuration is required for cost '{functionCost.CostName}'.");
        }

        // Provider type is the discriminator. Structural trial deserialization is unsafe:
        // a config with optional/default members can accept an unrelated JSON shape.
        try
        {
            return functionCost.ProviderType switch
            {
                FunctionProviderType.Exa => CalculateExaHybridCost(
                    DeserializeHybridConfig(
                        functionCost,
                        FunctionsJsonContext.Default.ExaHybridPricingConfig), usage),
                FunctionProviderType.Tavily => CalculateTavilySearchCost(
                    DeserializeHybridConfig(
                        functionCost,
                        FunctionsJsonContext.Default.TavilySearchPricingConfig), usage),
                FunctionProviderType.Perplexity => CalculatePerplexityHybridCost(
                    DeserializeHybridConfig(
                        functionCost,
                        FunctionsJsonContext.Default.PerplexityHybridPricingConfig), usage),
                _ => throw new InvalidOperationException(
                    $"Hybrid pricing is not supported for provider {functionCost.ProviderType} on cost '{functionCost.CostName}'.")
            };
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception,
                "Invalid {ProviderType} hybrid pricing configuration for cost {CostName}.",
                functionCost.ProviderType, functionCost.CostName);
            throw new InvalidOperationException(
                $"Invalid {functionCost.ProviderType} hybrid pricing configuration for cost '{functionCost.CostName}'.",
                exception);
        }
    }

    private static T DeserializeHybridConfig<T>(
        FunctionCost functionCost,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        return JsonSerializer.Deserialize(functionCost.PricingConfiguration!, jsonTypeInfo)
            ?? throw new JsonException($"Hybrid pricing configuration for '{functionCost.CostName}' deserialized to null.");
    }

    private decimal CalculatePerplexityHybridCost(
        PerplexityHybridPricingConfig config,
        FunctionExecutionUsage usage)
    {
        decimal tokenCost;

        if (usage.InputTokensConsumed.HasValue || usage.OutputTokensConsumed.HasValue)
        {
            tokenCost = ((usage.InputTokensConsumed ?? 0) * config.InputTokenCostPerMillion
                + (usage.OutputTokensConsumed ?? 0) * config.OutputTokenCostPerMillion) / 1_000_000m;
        }
        else
        {
            // Older clients only report a combined count. Charge it at the higher rate so
            // incomplete usage data cannot turn into an undercharge.
            tokenCost = (usage.TokensConsumed ?? 0)
                * Math.Max(config.InputTokenCostPerMillion, config.OutputTokenCostPerMillion)
                / 1_000_000m;
        }

        return config.BaseRequestCost + tokenCost;
    }

    /// <summary>
    /// Calculates cost using Exa.ai hybrid pricing rules.
    /// </summary>
    /// <param name="config">The Exa hybrid pricing configuration.</param>
    /// <param name="usage">The usage data.</param>
    /// <returns>The calculated cost.</returns>
    /// <remarks>
    /// Exa pricing breakdown:
    /// 1. Search cost: Depends on search type (neural/keyword) and result count tier
    /// 2. Text extraction: $0.001 per page
    /// 3. Highlights extraction: $0.001 per page
    /// 4. Summary generation: $0.001 per page
    ///
    /// Example: Neural search with 30 results, all with text and highlights:
    /// - Neural search (26-100 tier): $0.025
    /// - Text extraction: 30 × $0.001 = $0.030
    /// - Highlights extraction: 30 × $0.001 = $0.030
    /// - Total: $0.085
    /// </remarks>
    private decimal CalculateExaHybridCost(ExaHybridPricingConfig config, FunctionExecutionUsage usage)
    {
        var isContentRetrieval = usage.Metadata?.TryGetValue("operation", out var operation) == true
            && string.Equals(operation?.ToString(), "contents", StringComparison.OrdinalIgnoreCase);

        // Search and get-contents are distinct billable operations. A contents response
        // has no resolved search type because no search request was made.
        decimal requestCost = isContentRetrieval
            ? CalculateExaContentRetrievalCost(config, usage)
            : CalculateExaSearchCost(config, usage);

        // Both operations may also request text, highlights, or summaries.
        decimal contentCost = CalculateExaContentExtractionCost(config, usage);
        decimal totalCost = requestCost + contentCost;

        _logger.LogDebug("Exa hybrid cost breakdown: Request=${RequestCost}, Content=${ContentCost}, Total=${TotalCost}",
            requestCost, contentCost, totalCost);

        return totalCost;
    }

    /// <summary>
    /// Calculates Exa search cost based on search type and result count.
    /// </summary>
    private decimal CalculateExaSearchCost(ExaHybridPricingConfig config, FunctionExecutionUsage usage)
    {
        var searchType = usage.SearchType?.ToLowerInvariant() ?? "auto";
        var resultCount = usage.ResultCount ?? 0;

        decimal searchCost;

        switch (searchType)
        {
            case "neural":
                // Determine tier based on result count
                if (resultCount <= (config.SearchCosts?.Neural?.Tier1.MaxResults ?? 25))
                {
                    searchCost = config.SearchCosts?.Neural?.Tier1.Cost ?? 0m;
                    _logger.LogDebug("Neural search tier 1 (1-{Max} results): ${Cost}",
                        config.SearchCosts?.Neural?.Tier1.MaxResults ?? 25, searchCost);
                }
                else
                {
                    searchCost = config.SearchCosts?.Neural?.Tier2.Cost ?? 0m;
                    _logger.LogDebug("Neural search tier 2 ({Min}+ results): ${Cost}",
                        (config.SearchCosts?.Neural?.Tier1.MaxResults ?? 25) + 1, searchCost);
                }
                break;

            case "keyword":
                searchCost = config.SearchCosts?.Keyword?.Cost ?? 0m;
                _logger.LogDebug("Keyword search (any results): ${Cost}", searchCost);
                break;

            case "auto":
                // Auto mode: use conservative estimate (keyword pricing by default, or neural tier 1 if configured)
                if (config.SearchCosts?.Auto?.FallbackToKeyword == true)
                {
                    searchCost = config.SearchCosts?.Keyword?.Cost ?? 0m;
                    _logger.LogDebug("Auto search (fallback to keyword pricing): ${Cost}", searchCost);
                }
                else
                {
                    // Conservative: assume neural tier 1
                    searchCost = config.SearchCosts?.Neural?.Tier1.Cost ?? 0m;
                    _logger.LogDebug("Auto search (fallback to neural tier 1 pricing): ${Cost}", searchCost);
                }
                break;

            default:
                _logger.LogWarning("Unknown search type '{SearchType}', defaulting to keyword pricing", searchType);
                searchCost = config.SearchCosts?.Keyword?.Cost ?? 0m;
                break;
        }

        return searchCost;
    }

    /// <summary>
    /// Calculates Exa get-contents cost based on the number of returned pages.
    /// </summary>
    private decimal CalculateExaContentRetrievalCost(ExaHybridPricingConfig config, FunctionExecutionUsage usage)
    {
        var pages = Math.Max(usage.ResultCount ?? 0, 0);
        var costPer1000Pages = config.ContentRetrievalCosts?.CostPer1000Pages ?? 0m;
        var retrievalCost = pages * costPer1000Pages / 1_000m;

        _logger.LogDebug("Content retrieval: {Pages} pages × ${CostPer1000Pages}/1000 = ${Cost}",
            pages, costPer1000Pages, retrievalCost);

        return retrievalCost;
    }

    /// <summary>
    /// Calculates Exa content extraction costs (text, highlights, summary).
    /// </summary>
    private decimal CalculateExaContentExtractionCost(ExaHybridPricingConfig config, FunctionExecutionUsage usage)
    {
        decimal totalContentCost = 0m;

        // Text extraction cost
        if (usage.TextPagesExtracted.HasValue && usage.TextPagesExtracted.Value > 0)
        {
            var textCost = usage.TextPagesExtracted.Value * config.ContentExtractionCosts.Text;
            totalContentCost += textCost;
            _logger.LogDebug("Text extraction: {Pages} pages × ${CostPerPage} = ${Cost}",
                usage.TextPagesExtracted.Value, config.ContentExtractionCosts.Text, textCost);
        }

        // Highlights extraction cost
        if (usage.HighlightPagesExtracted.HasValue && usage.HighlightPagesExtracted.Value > 0)
        {
            var highlightsCost = usage.HighlightPagesExtracted.Value * config.ContentExtractionCosts.Highlights;
            totalContentCost += highlightsCost;
            _logger.LogDebug("Highlights extraction: {Pages} pages × ${CostPerPage} = ${Cost}",
                usage.HighlightPagesExtracted.Value, config.ContentExtractionCosts.Highlights, highlightsCost);
        }

        // Summary generation cost
        if (usage.SummaryPagesGenerated.HasValue && usage.SummaryPagesGenerated.Value > 0)
        {
            var summaryCost = usage.SummaryPagesGenerated.Value * config.ContentExtractionCosts.Summary;
            totalContentCost += summaryCost;
            _logger.LogDebug("Summary generation: {Pages} pages × ${CostPerPage} = ${Cost}",
                usage.SummaryPagesGenerated.Value, config.ContentExtractionCosts.Summary, summaryCost);
        }

        return totalContentCost;
    }

    /// <summary>
    /// Calculates cost using Tavily search pricing rules.
    /// </summary>
    /// <param name="config">The Tavily pricing configuration.</param>
    /// <param name="usage">The usage data.</param>
    /// <returns>The calculated cost.</returns>
    /// <remarks>
    /// Tavily pricing breakdown:
    /// 1. Base search cost: Depends on search depth (basic: 1 credit, advanced: 2 credits)
    /// 2. Auto-parameters addon: 2 credits (if enabled)
    /// 3. All content extraction included in base cost (images, answer, raw content)
    ///
    /// Example: Advanced search with auto-parameters:
    /// - Advanced search: 2 credits × $0.008 = $0.016
    /// - Auto-parameters: 2 credits × $0.008 = $0.016
    /// - Total: $0.032
    ///
    /// Unlike Exa, Tavily does NOT charge per result or for content extraction.
    /// </remarks>
    private decimal CalculateTavilySearchCost(TavilySearchPricingConfig config, FunctionExecutionUsage usage)
    {
        decimal totalCost = 0m;
        int totalCredits = 0;

        // 1. Calculate base search cost
        var searchDepth = usage.SearchType?.ToLowerInvariant() ?? "basic";
        int searchCredits = searchDepth == "advanced"
            ? config.AdvancedSearchCredits
            : config.BasicSearchCredits;

        totalCredits += searchCredits;
        _logger.LogDebug("Tavily {SearchDepth} search: {Credits} credits",
            searchDepth, searchCredits);

        // 2. Add auto-parameters cost if enabled
        if (usage.Metadata?.TryGetValue("autoParametersEnabled", out var autoParamsObj) == true)
        {
            if (autoParamsObj is bool autoParams && autoParams)
            {
                var autoParamsCredits = config.AutoParametersCredits ?? 0;
                totalCredits += autoParamsCredits;
                _logger.LogDebug("Tavily auto-parameters: {Credits} credits", autoParamsCredits);
            }
        }

        // 3. Calculate base cost from credits
        totalCost = totalCredits * config.CostPerCredit;

        // 4. Optional: Charge for answer generation separately (future-proofing)
        if (config.ChargeForAnswerGeneration &&
            usage.Metadata?.ContainsKey("answerGenerated") == true)
        {
            var answerCost = config.AnswerGenerationCost ?? 0m;
            totalCost += answerCost;
            _logger.LogDebug("Tavily answer generation: ${Cost}", answerCost);
        }

        // 5. Optional: Charge for images separately (future-proofing)
        if (config.ChargeForImageResults &&
            usage.Metadata?.TryGetValue("imageResults", out var imageCountObj) == true)
        {
            if (imageCountObj is int imageCount && imageCount > 0)
            {
                var imageCost = imageCount * (config.CostPerImage ?? 0m);
                totalCost += imageCost;
                _logger.LogDebug("Tavily image results: {Count} images × ${CostPerImage} = ${Cost}",
                    imageCount, config.CostPerImage ?? 0m, imageCost);
            }
        }

        _logger.LogDebug("Tavily cost breakdown: {Credits} credits × ${CostPerCredit} = ${TotalCost}",
            totalCredits, config.CostPerCredit, totalCost);

        return totalCost;
    }
}
