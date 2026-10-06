using System.Text.Json.Serialization;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Providers.Configuration;
using ConduitLLM.Providers.Helpers;
using CoreModels = ConduitLLM.Core.Models;
using CoreUtils = ConduitLLM.Core.Utilities;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.OpenRouter
{
    public partial class OpenRouterClient
    {
        private Func<string, string, int, Task>? _progressCallback;

        /// <summary>
        /// Sets a progress callback invoked as the async video job advances. Discovered by the video
        /// orchestrator via reflection (same <c>SetProgressCallback</c> contract MiniMax uses).
        /// </summary>
        public void SetProgressCallback(Func<string, string, int, Task> callback) => _progressCallback = callback;

        /// <summary>
        /// Generates a video via OpenRouter's async video API: <c>POST /api/v1/videos</c> (202 + job id),
        /// poll <c>GET /api/v1/videos/{id}</c> until completed, then return the unsigned download URL for
        /// the media pipeline (UrlMediaProcessor) to fetch and store.
        /// </summary>
        /// <remarks>
        /// Duck-typed (not on ILLMClient) so the video orchestrator discovers it by reflection
        /// (Name == "CreateVideoAsync" with 3 parameters), matching the MiniMax/Replicate pattern.
        /// OpenRouter's unsigned URLs require no auth header, so UrlMediaProcessor can download them.
        /// </remarks>
        public async Task<CoreModels.VideoGenerationResponse> CreateVideoAsync(
            CoreModels.VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateVideo");

            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);
                var headers = CreateStandardHeaders(apiKey);

                var submitBody = new Dictionary<string, object?>
                {
                    ["model"] = request.Model ?? ProviderModelId,
                    ["prompt"] = request.Prompt
                };
                if (request.Duration.HasValue) submitBody["duration"] = request.Duration.Value;
                if (request.Seed.HasValue) submitBody["seed"] = request.Seed.Value;
                if (!string.IsNullOrEmpty(request.Size)) submitBody["size"] = request.Size;
                if (request.ExtensionData != null)
                {
                    foreach (var kvp in request.ExtensionData)
                    {
                        if (kvp.Key == "stream") continue;
                        if (!submitBody.ContainsKey(kvp.Key)) submitBody[kvp.Key] = kvp.Value;
                    }
                }

                var submit = await PostJsonAsync<Dictionary<string, object?>, OpenRouterVideoSubmitResponse>(
                    client,
                    $"{BaseUrl}/videos",
                    submitBody,
                    apiKey,
                    cancellationToken);

                var jobId = submit.Id;
                if (string.IsNullOrEmpty(jobId))
                    throw new LLMCommunicationException("OpenRouter video generation did not return a job id.");

                var options = new PollingOptions(
                    InitialDelay: TimeSpan.FromSeconds(2),
                    MaxDelay: TimeSpan.FromSeconds(30),
                    Timeout: TimeSpan.FromSeconds(ProviderTimeouts.VideoPollingSeconds()),
                    Backoff: BackoffStrategy.ExponentialWithJitter,
                    MaxConsecutiveTransientErrors: 3,
                    BackoffMultiplier: 1.5,
                    JitterMilliseconds: 500);

                using var pollScope = BeginPollingScope("CreateVideo");
                var status = await AsyncJobPoller.PollAsync(
                    fetchStatus: ct => CoreUtils.HttpClientHelper.GetJsonAsync<OpenRouterVideoStatus>(
                        client,
                        $"{BaseUrl}/videos/{jobId}",
                        Serialization.ProvidersJsonContext.Default.OpenRouterVideoStatus,
                        headers,
                        Logger,
                        ct),
                    classify: ClassifyVideoStatus,
                    extractSuccess: s => s,
                    extractFailure: s => new LLMCommunicationException($"OpenRouter video generation failed for job {jobId}."),
                    options: options,
                    logger: Logger,
                    cancellationToken: cancellationToken,
                    onProgress: async (poll, s) =>
                    {
                        if (_progressCallback is not null && poll.State == JobState.InProgress)
                            await _progressCallback(jobId, s.Status ?? "in_progress", 0);
                    },
                    operationName: $"OpenRouter video generation {jobId}",
                    instrumentation: pollScope);

                var url = status.UnsignedUrls?.FirstOrDefault();
                if (string.IsNullOrEmpty(url))
                    throw new LLMCommunicationException($"OpenRouter video job {jobId} completed without a downloadable URL.");

                return new CoreModels.VideoGenerationResponse
                {
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = request.Model,
                    Data = new List<CoreModels.VideoData>
                    {
                        new()
                        {
                            Url = url,
                            Metadata = new CoreModels.VideoMetadata
                            {
                                Format = "mp4",
                                MimeType = "video/mp4",
                                Duration = request.Duration ?? 0
                            }
                        }
                    },
                    Usage = new CoreModels.VideoGenerationUsage
                    {
                        VideosGenerated = 1,
                        TotalDurationSeconds = request.Duration ?? 0,
                        EstimatedCost = status.Usage?.Cost
                    }
                };
            }, "CreateVideo", cancellationToken);
        }

        private static JobState ClassifyVideoStatus(OpenRouterVideoStatus status) =>
            status.Status?.ToLowerInvariant() switch
            {
                "completed" => JobState.Succeeded,
                "failed" => JobState.Failed,
                _ => JobState.InProgress   // pending / in_progress / queued / unknown → keep polling
            };

        internal record OpenRouterVideoSubmitResponse
        {
            [JsonPropertyName("id")]
            public string? Id { get; init; }
        }

        internal record OpenRouterVideoStatus
        {
            [JsonPropertyName("id")]
            public string? Id { get; init; }

            [JsonPropertyName("status")]
            public string? Status { get; init; }

            [JsonPropertyName("generation_id")]
            public string? GenerationId { get; init; }

            [JsonPropertyName("unsigned_urls")]
            public List<string>? UnsignedUrls { get; init; }

            [JsonPropertyName("usage")]
            public OpenRouterVideoUsage? Usage { get; init; }
        }

        internal record OpenRouterVideoUsage
        {
            [JsonPropertyName("cost")]
            public decimal? Cost { get; init; }

            [JsonPropertyName("is_byok")]
            public bool? IsByok { get; init; }
        }
    }
}
