using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers.Exa.Models;

namespace ConduitLLM.Functions.Providers.Exa;

/// <summary>
/// Usage calculation for ExaClient.
/// </summary>
public partial class ExaClient
{
    /// <inheritdoc />
    public FunctionExecutionUsage CalculateUsageFromResponse(
        Dictionary<string, object> parameters,
        Interfaces.FunctionExecutionResult result)
    {
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.ResponseJson))
        {
            _logger.LogWarning("Cannot calculate usage from failed execution");
            return new FunctionExecutionUsage
            {
                ResultCount = 0,
                ExecutionDuration = result.Duration
            };
        }

        try
        {
            // Determine response type based on parameters
            if (parameters.ContainsKey("urls"))
            {
                // Get contents response
                return CalculateUsageFromContentsResponse(result);
            }
            else
            {
                // Search response
                return CalculateUsageFromSearchResponse(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating usage from Exa response");
            return new FunctionExecutionUsage
            {
                ResultCount = 0,
                ExecutionDuration = result.Duration
            };
        }
    }

    /// <summary>
    /// Calculates usage from a search response.
    /// </summary>
    private FunctionExecutionUsage CalculateUsageFromSearchResponse(Interfaces.FunctionExecutionResult result)
    {
        var exaResponse = JsonSerializer.Deserialize(
            result.ResponseJson!,
            FunctionProviderJsonContext.Default.ExaSearchResponse);

        if (exaResponse == null)
        {
            _logger.LogError("Failed to deserialize Exa search response for usage calculation");
            return new FunctionExecutionUsage
            {
                ResultCount = 0,
                ExecutionDuration = result.Duration
            };
        }

        // Calculate usage dimensions
        var usage = new FunctionExecutionUsage
        {
            ResultCount = exaResponse.Results.Count,
            SearchType = exaResponse.ResolvedSearchType,
            ExecutionDuration = result.Duration,
            Metadata = new Dictionary<string, object>
            {
                ["requestId"] = exaResponse.RequestId
            }
        };

        // Determine content extraction counts based on actual results
        usage.TextPagesExtracted = CountTextExtractions(exaResponse.Results);
        usage.HighlightPagesExtracted = CountHighlightExtractions(exaResponse.Results);
        usage.SummaryPagesGenerated = CountSummaryGenerations(exaResponse.Results);

        // Store provider-reported cost for reconciliation
        if (exaResponse.CostDollars != null)
        {
            usage.ProviderReportedCost = exaResponse.CostDollars.Total;
            usage.Metadata["exaCostBreakdown"] = new
            {
                search = exaResponse.CostDollars.Search,
                getText = exaResponse.CostDollars.GetText,
                getHighlights = exaResponse.CostDollars.GetHighlights,
                getSummary = exaResponse.CostDollars.GetSummary,
                total = exaResponse.CostDollars.Total
            };
        }

        _logger.LogDebug("Calculated Exa search usage: SearchType={SearchType}, Results={ResultCount}, Text={TextPages}, Highlights={HighlightPages}, Summary={SummaryPages}",
            usage.SearchType, usage.ResultCount, usage.TextPagesExtracted, usage.HighlightPagesExtracted, usage.SummaryPagesGenerated);

        return usage;
    }

    /// <summary>
    /// Calculates usage from a get contents response.
    /// </summary>
    private FunctionExecutionUsage CalculateUsageFromContentsResponse(Interfaces.FunctionExecutionResult result)
    {
        var exaResponse = JsonSerializer.Deserialize(
            result.ResponseJson!,
            FunctionProviderJsonContext.Default.ExaContentsResponse);

        if (exaResponse == null)
        {
            _logger.LogError("Failed to deserialize Exa contents response for usage calculation");
            return new FunctionExecutionUsage
            {
                ResultCount = 0,
                ExecutionDuration = result.Duration
            };
        }

        // Count total pages including subpages recursively
        var totalPages = CountTotalPagesForUsage(exaResponse.Results);

        // Calculate usage dimensions
        var usage = new FunctionExecutionUsage
        {
            ResultCount = totalPages, // Total pages including subpages (billed per piece of content)
            ExecutionDuration = result.Duration,
            Metadata = new Dictionary<string, object>
            {
                ["operation"] = "contents",
                ["requestId"] = exaResponse.RequestId,
                ["urlCount"] = exaResponse.Results.Count,
                ["totalPages"] = totalPages
            }
        };

        // Determine content extraction counts based on actual results (includes subpages)
        usage.TextPagesExtracted = CountTextExtractionsRecursive(exaResponse.Results);
        usage.HighlightPagesExtracted = CountHighlightExtractionsRecursive(exaResponse.Results);
        usage.SummaryPagesGenerated = CountSummaryGenerationsRecursive(exaResponse.Results);

        // Store provider-reported cost for reconciliation
        if (exaResponse.CostDollars != null)
        {
            usage.ProviderReportedCost = exaResponse.CostDollars.Total;
            usage.Metadata["exaCostBreakdown"] = new
            {
                getText = exaResponse.CostDollars.GetText,
                getHighlights = exaResponse.CostDollars.GetHighlights,
                getSummary = exaResponse.CostDollars.GetSummary,
                total = exaResponse.CostDollars.Total
            };
        }

        _logger.LogDebug("Calculated Exa contents usage: URLs={UrlCount}, TotalPages={TotalPages}, Text={TextPages}, Highlights={HighlightPages}, Summary={SummaryPages}",
            exaResponse.Results.Count, totalPages, usage.TextPagesExtracted, usage.HighlightPagesExtracted, usage.SummaryPagesGenerated);

        return usage;
    }

    /// <summary>
    /// Counts how many results have text content extracted.
    /// </summary>
    private int CountTextExtractions(List<ExaResult> results)
    {
        return results.Count(r => !string.IsNullOrWhiteSpace(r.Text));
    }

    /// <summary>
    /// Counts how many results have highlights extracted.
    /// </summary>
    private int CountHighlightExtractions(List<ExaResult> results)
    {
        return results.Count(r => r.Highlights != null && r.Highlights.Any());
    }

    /// <summary>
    /// Counts how many results have summaries generated.
    /// </summary>
    private int CountSummaryGenerations(List<ExaResult> results)
    {
        return results.Count(r => !string.IsNullOrWhiteSpace(r.Summary));
    }

    /// <summary>
    /// Counts total pages including subpages recursively for usage calculation.
    /// </summary>
    private int CountTotalPagesForUsage(List<ExaResult> results)
    {
        var count = results.Count;

        foreach (var result in results)
        {
            if (result.Subpages != null && result.Subpages.Any())
            {
                count += CountTotalPagesForUsage(result.Subpages);
            }
        }

        return count;
    }

    /// <summary>
    /// Counts text extractions recursively including subpages.
    /// </summary>
    private int CountTextExtractionsRecursive(List<ExaResult> results)
    {
        var count = results.Count(r => !string.IsNullOrWhiteSpace(r.Text));

        foreach (var result in results)
        {
            if (result.Subpages != null && result.Subpages.Any())
            {
                count += CountTextExtractionsRecursive(result.Subpages);
            }
        }

        return count;
    }

    /// <summary>
    /// Counts highlight extractions recursively including subpages.
    /// </summary>
    private int CountHighlightExtractionsRecursive(List<ExaResult> results)
    {
        var count = results.Count(r => r.Highlights != null && r.Highlights.Any());

        foreach (var result in results)
        {
            if (result.Subpages != null && result.Subpages.Any())
            {
                count += CountHighlightExtractionsRecursive(result.Subpages);
            }
        }

        return count;
    }

    /// <summary>
    /// Counts summary generations recursively including subpages.
    /// </summary>
    private int CountSummaryGenerationsRecursive(List<ExaResult> results)
    {
        var count = results.Count(r => !string.IsNullOrWhiteSpace(r.Summary));

        foreach (var result in results)
        {
            if (result.Subpages != null && result.Subpages.Any())
            {
                count += CountSummaryGenerationsRecursive(result.Subpages);
            }
        }

        return count;
    }
}
