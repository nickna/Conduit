using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Providers.Exa.Models;

namespace ConduitLLM.Functions.Providers.Exa;

/// <summary>
/// Search operations for ExaClient.
/// </summary>
public partial class ExaClient
{
    /// <summary>
    /// Executes the search operation.
    /// </summary>
    protected async Task<FunctionExecutionResult> ExecuteSearchAsync(
        Dictionary<string, object> parameters,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Validate and map parameters to Exa request
            var exaRequest = MapToExaSearchRequest(parameters);

            _logger.LogInformation("Executing Exa search: Query='{Query}', Type={Type}, NumResults={NumResults}",
                exaRequest.Query, exaRequest.Type ?? "auto", exaRequest.NumResults ?? 10);

            using var client = CreateHttpClient(apiKey);

            // Make API request
            var response = await client.PostAsJsonAsync(
                "/search",
                exaRequest,
                FunctionProviderJsonContext.Default.ExaSearchRequest,
                cancellationToken);

            stopwatch.Stop();

            // Handle response
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw HandleHttpError(response.StatusCode, errorBody);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var exaResponse = JsonSerializer.Deserialize(
                responseBody,
                FunctionProviderJsonContext.Default.ExaSearchResponse);

            if (exaResponse == null)
            {
                throw new InvalidOperationException("Failed to deserialize Exa API response");
            }

            _logger.LogInformation("Exa search completed: RequestId={RequestId}, Results={ResultCount}, SearchType={SearchType}, Duration={Duration}ms",
                exaResponse.RequestId, exaResponse.Results.Count, exaResponse.ResolvedSearchType, stopwatch.ElapsedMilliseconds);

            // Log cost reconciliation if available
            if (exaResponse.CostDollars != null)
            {
                var searchCost = exaResponse.CostDollars.Search?.Values.Sum() ?? 0;
                _logger.LogDebug("Exa reported cost: ${Total} (Search=${Search}, Text=${Text}, Highlights=${Highlights}, Summary=${Summary})",
                    exaResponse.CostDollars.Total ?? 0,
                    searchCost,
                    exaResponse.CostDollars.GetText ?? 0,
                    exaResponse.CostDollars.GetHighlights ?? 0,
                    exaResponse.CostDollars.GetSummary ?? 0);
            }

            return new FunctionExecutionResult
            {
                IsSuccess = true,
                ResponseJson = responseBody,
                Duration = stopwatch.Elapsed,
                HttpStatusCode = (int)response.StatusCode,
                Metadata = new Dictionary<string, object>
                {
                    ["requestId"] = exaResponse.RequestId,
                    ["resolvedSearchType"] = exaResponse.ResolvedSearchType,
                    ["resultCount"] = exaResponse.Results.Count
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
            _logger.LogError(ex, "Exa search timed out after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = "Exa API request timed out",
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Exa search failed after {ElapsedMs}ms: {Error}", stopwatch.ElapsedMilliseconds, ex.Message);

            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }
}
