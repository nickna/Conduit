using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Providers.Exa.Models;
using ConduitLLM.Functions.Utilities;

namespace ConduitLLM.Functions.Providers.Exa;

/// <summary>
/// Get contents operations for ExaClient.
/// </summary>
public partial class ExaClient
{
    /// <summary>
    /// Executes the get contents operation to retrieve content from specific URLs.
    /// </summary>
    protected async Task<FunctionExecutionResult> ExecuteGetContentsAsync(
        Dictionary<string, object> parameters,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Validate and map parameters to Exa request
            var exaRequest = MapToExaContentsRequest(parameters);

            _logger.LogInformation("Executing Exa get contents: Urls={UrlCount}, Text={Text}, Highlights={Highlights}, Summary={Summary}, Subpages={Subpages}",
                exaRequest.Urls.Count,
                exaRequest.Text != null,
                exaRequest.Highlights != null,
                exaRequest.Summary != null,
                exaRequest.Subpages ?? 0);

            using var client = CreateHttpClient(apiKey);

            // Make API request
            var response = await client.PostAsJsonAsync(
                "/contents",
                exaRequest,
                FunctionProviderJsonContext.Default.ExaContentsRequest,
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
                FunctionProviderJsonContext.Default.ExaContentsResponse);

            if (exaResponse == null)
            {
                throw new InvalidOperationException("Failed to deserialize Exa get contents API response");
            }

            // Count total pages including subpages
            var totalPages = CountTotalPages(exaResponse.Results);

            _logger.LogInformation("Exa get contents completed: RequestId={RequestId}, Results={ResultCount}, TotalPages={TotalPages}, Duration={Duration}ms",
                exaResponse.RequestId, exaResponse.Results.Count, totalPages, stopwatch.ElapsedMilliseconds);

            // Log cost reconciliation if available
            if (exaResponse.CostDollars != null)
            {
                _logger.LogDebug("Exa reported cost: ${Total} (Text=${Text}, Highlights=${Highlights}, Summary=${Summary})",
                    exaResponse.CostDollars.Total ?? 0,
                    exaResponse.CostDollars.GetText ?? 0,
                    exaResponse.CostDollars.GetHighlights ?? 0,
                    exaResponse.CostDollars.GetSummary ?? 0);
            }

            // Log failures if any
            if (exaResponse.Statuses != null)
            {
                var failures = exaResponse.Statuses.Where(s => !s.Success).ToList();
                if (failures.Any())
                {
                    _logger.LogWarning("Exa get contents had {FailureCount} failed URLs: {FailedUrls}",
                        failures.Count,
                        string.Join(", ", failures.Select(f => $"{f.Url} ({f.Error})")));
                }
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
                    ["resultCount"] = exaResponse.Results.Count,
                    ["totalPages"] = totalPages,
                    ["hasContext"] = !string.IsNullOrWhiteSpace(exaResponse.Context),
                    ["failureCount"] = exaResponse.Statuses?.Count(s => !s.Success) ?? 0
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
            _logger.LogError(ex, "Exa get contents timed out after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = "Exa get contents API request timed out",
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Exa get contents failed after {ElapsedMs}ms: {Error}", stopwatch.ElapsedMilliseconds, ex.Message);

            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    /// <summary>
    /// Validates request parameters and converts to ExaContentsRequest.
    /// </summary>
    protected ExaContentsRequest MapToExaContentsRequest(Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("urls", out var urlsObj) || urlsObj == null)
        {
            throw new ArgumentException("Parameter 'urls' is required", nameof(parameters));
        }

        // Handle urls as JsonElement (from JSON deserialization), List<string>, or comma-separated string
        List<string> urls;

        if (urlsObj is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                urls = jsonElement.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            else if (jsonElement.ValueKind == JsonValueKind.String)
            {
                var urlString = jsonElement.GetString() ?? string.Empty;
                urls = urlString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else
            {
                throw new ArgumentException("Parameter 'urls' must be a List<string> or comma-separated string (Parameter 'parameters')");
            }
        }
        else if (urlsObj is List<string> urlList)
        {
            urls = urlList;
        }
        else if (urlsObj is string urlString)
        {
            urls = urlString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        else
        {
            throw new ArgumentException("Parameter 'urls' must be a List<string> or comma-separated string (Parameter 'parameters')");
        }

        if (urls.Count == 0)
        {
            throw new ArgumentException("Parameter 'urls' must contain at least one URL", nameof(parameters));
        }

        var request = new ExaContentsRequest
        {
            Urls = urls
        };

        // Map optional parameters, converting JsonElement to proper types
        if (parameters.TryGetValue("text", out var textObj))
            request.Text = JsonElementConverter.ConvertJsonElement(textObj);

        if (parameters.TryGetValue("highlights", out var highlightsObj))
            request.Highlights = JsonElementConverter.ConvertJsonElement(highlightsObj);

        if (parameters.TryGetValue("summary", out var summaryObj))
            request.Summary = JsonElementConverter.ConvertJsonElement(summaryObj);

        if (parameters.TryGetValue("livecrawl", out var livecrawlObj))
        {
            var converted = JsonElementConverter.ConvertJsonElement(livecrawlObj);
            request.Livecrawl = converted?.ToString();
        }

        if (parameters.TryGetValue("livecrawlTimeout", out var livecrawlTimeoutObj))
        {
            var converted = JsonElementConverter.ConvertJsonElement(livecrawlTimeoutObj);
            request.LivecrawlTimeout = Convert.ToInt32(converted);
        }

        if (parameters.TryGetValue("subpages", out var subpagesObj))
        {
            var converted = JsonElementConverter.ConvertJsonElement(subpagesObj);
            request.Subpages = Convert.ToInt32(converted);
        }

        if (parameters.TryGetValue("subpageTarget", out var subpageTargetObj))
            request.SubpageTarget = JsonElementConverter.ConvertJsonElement(subpageTargetObj);

        if (parameters.TryGetValue("extras", out var extrasObj))
            request.Extras = JsonElementConverter.ConvertJsonElement(extrasObj);

        if (parameters.TryGetValue("context", out var contextObj))
            request.Context = JsonElementConverter.ConvertJsonElement(contextObj);

        return request;
    }

    /// <summary>
    /// Counts total pages including subpages recursively.
    /// </summary>
    private int CountTotalPages(List<ExaResult> results)
    {
        var count = results.Count;

        foreach (var result in results)
        {
            if (result.Subpages != null && result.Subpages.Any())
            {
                count += CountTotalPages(result.Subpages);
            }
        }

        return count;
    }
}
