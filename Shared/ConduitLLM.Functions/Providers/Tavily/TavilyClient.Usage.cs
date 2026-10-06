using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers.Tavily.Models;
using ConduitLLM.Functions.Utilities;

namespace ConduitLLM.Functions.Providers.Tavily;

/// <summary>
/// Usage calculation for TavilyClient.
/// </summary>
public partial class TavilyClient
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
            var tavilyResponse = JsonSerializer.Deserialize(
                result.ResponseJson,
                FunctionProviderJsonContext.Default.TavilySearchResponse);

            if (tavilyResponse == null)
            {
                _logger.LogError("Failed to deserialize Tavily response for usage calculation");
                return new FunctionExecutionUsage
                {
                    ResultCount = 0,
                    ExecutionDuration = result.Duration
                };
            }

            // Calculate total results (text + images, per Option A)
            var textResults = tavilyResponse.Results.Count;
            var imageResults = tavilyResponse.Images?.Count ?? 0;
            var totalResults = textResults + imageResults;

            // Determine search depth from parameters
            var searchDepth = "basic"; // default
            if (parameters.TryGetValue("search_depth", out var depthObj))
            {
                searchDepth = depthObj?.ToString() ?? "basic";
            }

            // Build usage object
            var usage = new FunctionExecutionUsage
            {
                ResultCount = totalResults,
                SearchType = searchDepth,
                ExecutionDuration = result.Duration,
                Metadata = new Dictionary<string, object>
                {
                    ["requestId"] = tavilyResponse.RequestId ?? "",
                    ["textResults"] = textResults,
                    ["imageResults"] = imageResults,
                    ["responseTime"] = tavilyResponse.ResponseTime ?? 0
                }
            };

            // Track answer generation
            if (!string.IsNullOrWhiteSpace(tavilyResponse.Answer))
            {
                usage.Metadata["answerGenerated"] = true;
                usage.Metadata["answerLength"] = tavilyResponse.Answer.Length;
            }

            // Track raw content extraction
            var rawContentCount = tavilyResponse.Results.Count(r => !string.IsNullOrWhiteSpace(r.RawContent));
            if (rawContentCount > 0)
            {
                usage.TextPagesExtracted = rawContentCount;
                usage.Metadata["rawContentExtracted"] = rawContentCount;
            }

            // Track auto-parameters usage
            if (parameters.TryGetValue("auto_parameters", out var autoParamsObj)
                && JsonElementConverter.ConvertToBoolean(autoParamsObj) == true)
            {
                usage.Metadata["autoParametersEnabled"] = true;
            }

            // Track topic
            if (parameters.TryGetValue("topic", out var topicObj))
            {
                usage.Metadata["topic"] = topicObj?.ToString() ?? "general";
            }

            _logger.LogDebug("Calculated Tavily usage: SearchDepth={SearchDepth}, TotalResults={TotalResults} (Text={TextResults}, Images={ImageResults})",
                searchDepth, totalResults, textResults, imageResults);

            return usage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating usage from Tavily response");
            return new FunctionExecutionUsage
            {
                ResultCount = 0,
                ExecutionDuration = result.Duration
            };
        }
    }
}
