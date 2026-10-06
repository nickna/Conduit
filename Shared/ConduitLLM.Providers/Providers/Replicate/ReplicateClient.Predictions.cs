using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Providers.Helpers;
using ConduitLLM.Providers.Serialization;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Replicate
{
    public partial class ReplicateClient
    {
        /// <summary>
        /// Cancels a running prediction on Replicate.
        /// </summary>
        private async Task CancelPredictionAsync(string predictionId, string? apiKey)
        {
            try
            {
                using var client = CreateHttpClient(apiKey);
                using var response = await client.PostAsync($"predictions/{predictionId}/cancel", null);
                
                if (response.IsSuccessStatusCode)
                {
                    Logger.LogInformation("Successfully cancelled Replicate prediction {Id}", predictionId);
                }
                else
                {
                    Logger.LogWarning("Failed to cancel Replicate prediction {Id}: {StatusCode}", 
                        predictionId, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error cancelling Replicate prediction {Id}", predictionId);
            }
        }

        private async Task<ReplicatePredictionResponse> StartPredictionAsync(
            ReplicatePredictionRequest request,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            try
            {
                using var client = CreateHttpClient(apiKey);
                
                // Determine the endpoint based on the model ID format
                string endpoint;
                if (ProviderModelId.Contains('/') && !ProviderModelId.Contains(':'))
                {
                    // Model slug format (e.g., "bytedance/seedream-3")
                    // Use the models/{owner}/{name}/predictions endpoint (relative path)
                    endpoint = $"models/{ProviderModelId}/predictions";
                    // Remove the version field from the request since we're using the model endpoint
                    request.Version = null;
                    Logger.LogInformation("Using model endpoint: {Endpoint} for model {ModelId}", endpoint, ProviderModelId);
                }
                else
                {
                    // Version hash format (e.g., "a1b2c3...")
                    // Use the predictions endpoint with version in body (relative path)
                    endpoint = "predictions";
                    Logger.LogInformation("Using version endpoint: {Endpoint} with version {Version}", endpoint, request.Version);
                }
                
                Logger.LogInformation("Sending request to Replicate: {BaseUrl}{Endpoint}", client.BaseAddress, endpoint);
                using var response = await client.PostAsJsonAsync(
                    endpoint,
                    request,
                    ProvidersJsonContext.Default.ReplicatePredictionRequest,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await ReadErrorContentAsync(response, cancellationToken);
                    Logger.LogError("Replicate API prediction creation failed with status {StatusCode}. Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    throw new LLMCommunicationException(
                        $"Replicate prediction creation failed: {errorContent}",
                        response.StatusCode, errorContent);
                }

                var predictionResponse = await response.Content.ReadFromJsonAsync(
                    ProvidersJsonContext.Default.ReplicatePredictionResponse,
                    cancellationToken);

                if (predictionResponse == null)
                {
                    throw new LLMCommunicationException("Failed to deserialize Replicate prediction response");
                }

                return predictionResponse;
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, "HTTP request error communicating with Replicate API");
                throw new LLMCommunicationException($"HTTP request error communicating with Replicate API: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "JSON error processing Replicate response");
                throw new LLMCommunicationException("Error deserializing Replicate response", ex);
            }
            catch (ConduitException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An unexpected error occurred while starting Replicate prediction");
                throw new LLMCommunicationException($"An unexpected error occurred: {ex.Message}", ex);
            }
        }

        private Task<ReplicatePredictionResponse> PollPredictionUntilCompletedAsync(
            string predictionId,
            string? apiKey,
            CancellationToken cancellationToken,
            ProviderInstrumentation.PollingScope? instrumentation = null)
        {
            var options = new PollingOptions(
                InitialDelay: DefaultPollingInterval,
                MaxDelay: DefaultPollingInterval,
                Timeout: MaxPollingDuration,
                Backoff: BackoffStrategy.Fixed,
                MaxConsecutiveTransientErrors: null);

            Logger.LogInformation("Starting to poll prediction {PredictionId}, max duration: {MaxDuration}",
                predictionId, MaxPollingDuration);

            return AsyncJobPoller.PollAsync(
                fetchStatus: ct => FetchPredictionStatusAsync(predictionId, apiKey, ct),
                classify: ClassifyPredictionStatus,
                extractSuccess: prediction => prediction,
                extractFailure: prediction => ExtractPredictionFailure(prediction, predictionId),
                options: options,
                logger: Logger,
                cancellationToken: cancellationToken,
                onAbort: () => CancelPredictionAsync(predictionId, apiKey),
                operationName: $"Replicate prediction {predictionId}",
                instrumentation: instrumentation);
        }

        private async Task<ReplicatePredictionResponse> FetchPredictionStatusAsync(
            string predictionId,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            using var client = CreateHttpClient(apiKey);
            using var response = await client.GetAsync($"predictions/{predictionId}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await ReadErrorContentAsync(response, cancellationToken);
                Logger.LogError("Replicate API prediction polling failed with status {StatusCode}. Response: {ErrorContent}",
                    response.StatusCode, errorContent);
                throw new LLMCommunicationException(
                    $"Replicate prediction polling failed: {errorContent}",
                    response.StatusCode, errorContent);
            }

            var prediction = await response.Content.ReadFromJsonAsync(
                ProvidersJsonContext.Default.ReplicatePredictionResponse,
                cancellationToken);
            if (prediction == null)
            {
                throw new LLMCommunicationException("Failed to deserialize Replicate prediction response");
            }
            return prediction;
        }

        private static JobState ClassifyPredictionStatus(ReplicatePredictionResponse prediction) =>
            prediction.Status.ToLowerInvariant() switch
            {
                "succeeded" => JobState.Succeeded,
                "failed" or "canceled" => JobState.Failed,
                _ => JobState.InProgress,
            };

        private Exception ExtractPredictionFailure(ReplicatePredictionResponse prediction, string predictionId)
        {
            if (string.Equals(prediction.Status, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("Prediction {PredictionId} was canceled by Replicate", predictionId);
                return new LLMCommunicationException("Replicate prediction was canceled");
            }
            Logger.LogError("Prediction {PredictionId} failed: {Error}", predictionId, prediction.Error);
            return ClassifyReplicatePredictionError(prediction.Error, predictionId);
        }

        /// <summary>
        /// Classifies a Replicate prediction error into the appropriate exception type
        /// based on the error message content.
        /// </summary>
        private Exception ClassifyReplicatePredictionError(string? error, string predictionId)
        {
            if (string.IsNullOrEmpty(error))
            {
                return new LLMCommunicationException($"Replicate prediction {predictionId} failed with no error details");
            }

            var errorLower = error.ToLowerInvariant();

            // Authentication / authorization errors
            if (errorLower.Contains("invalid api token") || errorLower.Contains("unauthorized") ||
                errorLower.Contains("authentication") || errorLower.Contains("invalid token"))
            {
                return new LLMCommunicationException(
                    $"Replicate authentication error: {error}",
                    HttpStatusCode.Unauthorized, error);
            }

            // Billing / quota errors
            if (errorLower.Contains("insufficient") || errorLower.Contains("billing") ||
                errorLower.Contains("payment") || errorLower.Contains("quota") ||
                errorLower.Contains("credit"))
            {
                return new LLMCommunicationException(
                    $"Replicate billing error: {error}",
                    HttpStatusCode.PaymentRequired, error);
            }

            // Rate limiting
            if (errorLower.Contains("rate limit") || errorLower.Contains("too many requests") ||
                errorLower.Contains("throttl"))
            {
                return new RateLimitExceededException($"Replicate rate limit: {error}");
            }

            // Content policy
            if (errorLower.Contains("nsfw") || errorLower.Contains("content policy") ||
                errorLower.Contains("safety") || errorLower.Contains("moderation") ||
                errorLower.Contains("not allowed"))
            {
                return new InvalidRequestException($"Content policy violation: {error}", "content_policy_violation", "prompt");
            }

            // Model errors
            if (errorLower.Contains("model") && (errorLower.Contains("not found") || errorLower.Contains("does not exist")))
            {
                return new ModelNotFoundException(ProviderModelId, $"Replicate model error: {error}");
            }

            // Input validation
            if (errorLower.Contains("invalid input") || errorLower.Contains("validation") ||
                errorLower.Contains("invalid value") || errorLower.Contains("must be"))
            {
                return new InvalidRequestException($"Replicate input validation error: {error}");
            }

            // Service errors
            if (errorLower.Contains("service unavailable") || errorLower.Contains("internal error") ||
                errorLower.Contains("server error"))
            {
                return new ServiceUnavailableException($"Replicate service error: {error}");
            }

            // Default: unclassified provider error with the original message preserved
            return new LLMCommunicationException($"Replicate prediction failed: {error}");
        }
    }
}
