using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Providers.Tavily.Models;

namespace ConduitLLM.Functions.Providers.Tavily;

/// <summary>
/// Search operations for TavilyClient.
/// </summary>
public partial class TavilyClient
{
    /// <inheritdoc />
    public async Task<FunctionExecutionResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Validate and map parameters to Tavily request
            var tavilyRequest = MapToTavilySearchRequest(parameters);

            _logger.LogInformation("Executing Tavily search: Query='{Query}', Depth={Depth}, MaxResults={MaxResults}",
                tavilyRequest.Query, tavilyRequest.SearchDepth ?? "basic", tavilyRequest.MaxResults ?? 5);

            using var client = CreateHttpClient(apiKey);

            // Make API request
            var response = await client.PostAsJsonAsync(
                "/search",
                tavilyRequest,
                FunctionProviderJsonContext.Default.TavilySearchRequest,
                cancellationToken);

            stopwatch.Stop();

            // Handle response
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw HandleHttpError(response.StatusCode, errorBody);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var tavilyResponse = JsonSerializer.Deserialize(
                responseBody,
                FunctionProviderJsonContext.Default.TavilySearchResponse);

            if (tavilyResponse == null)
            {
                throw new InvalidOperationException("Failed to deserialize Tavily API response");
            }

            var totalResults = tavilyResponse.Results.Count + (tavilyResponse.Images?.Count ?? 0);

            _logger.LogInformation("Tavily search completed: RequestId={RequestId}, TextResults={TextResults}, Images={Images}, TotalResults={TotalResults}, Duration={Duration}ms",
                tavilyResponse.RequestId ?? "N/A",
                tavilyResponse.Results.Count,
                tavilyResponse.Images?.Count ?? 0,
                totalResults,
                stopwatch.ElapsedMilliseconds);

            // Log answer generation if present
            if (!string.IsNullOrWhiteSpace(tavilyResponse.Answer))
            {
                _logger.LogDebug("Tavily generated answer: {AnswerLength} characters",
                    tavilyResponse.Answer.Length);
            }

            return new FunctionExecutionResult
            {
                IsSuccess = true,
                ResponseJson = responseBody,
                Duration = stopwatch.Elapsed,
                HttpStatusCode = (int)response.StatusCode,
                Metadata = new Dictionary<string, object>
                {
                    ["requestId"] = tavilyResponse.RequestId ?? "",
                    ["textResultCount"] = tavilyResponse.Results.Count,
                    ["imageResultCount"] = tavilyResponse.Images?.Count ?? 0,
                    ["totalResultCount"] = totalResults,
                    ["answerGenerated"] = !string.IsNullOrWhiteSpace(tavilyResponse.Answer),
                    ["responseTime"] = tavilyResponse.ResponseTime ?? 0
                }
            };
        }
        catch (FunctionCommunicationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Tavily search timed out after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = "Tavily API request timed out",
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Tavily search failed after {ElapsedMs}ms: {Error}", stopwatch.ElapsedMilliseconds, ex.Message);

            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }
}
