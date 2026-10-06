using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Configuration;
using ConduitLLM.Providers.Helpers;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// MiniMaxClient partial class containing video generation functionality.
    /// </summary>
    public partial class MiniMaxClient
    {
        /// <summary>
        /// Creates a video based on the provided request.
        /// </summary>
        /// <param name="request">The video generation request containing the prompt and generation parameters.</param>
        /// <param name="apiKey">Optional API key override to use instead of the client's configured key.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The video generation response containing URLs or base64-encoded videos.</returns>
        public async Task<VideoGenerationResponse> CreateVideoAsync(
            VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateVideo");

            return await ExecuteApiRequestAsync(async () =>
            {
                using var httpClient = CreateVideoHttpClient(apiKey);
                
                // Create the request as a dictionary to support extension data
                var miniMaxRequest = new Dictionary<string, object?>
                {
                    ["model"] = request.Model ?? "video-01",
                    ["prompt"] = request.Prompt
                };

                // Add optional parameters with MiniMax-specific names
                if (request.Duration.HasValue)
                    miniMaxRequest["video_length"] = request.Duration.Value;
                if (!string.IsNullOrEmpty(request.Size))
                    miniMaxRequest["resolution"] = MapSizeToResolution(request.Size);

                // Pass through any extension data (model-specific parameters)
                if (request.ExtensionData != null)
                {
                    foreach (var kvp in request.ExtensionData)
                    {
                        // Don't override standard parameters
                        if (!miniMaxRequest.ContainsKey(kvp.Key))
                        {
                            miniMaxRequest[kvp.Key] = kvp.Value;
                        }
                    }
                }

                var endpoint = $"{_baseUrl}/v1/video_generation";
                
                // Create log-safe version of the request (excluding prompt content)
                var logSafeRequest = new Dictionary<string, object?>(miniMaxRequest);
                if (logSafeRequest.ContainsKey("prompt"))
                {
                    var promptLength = (miniMaxRequest["prompt"]?.ToString() ?? "").Length;
                    logSafeRequest["prompt"] = $"[REDACTED: {promptLength} chars]";
                }
                
                Logger.LogInformation("MiniMax video generation parameters: Model={Model}, Parameters={@Parameters}", 
                    miniMaxRequest["model"], logSafeRequest);
                
                var response = await SendMiniMaxJsonAsync<
                    Dictionary<string, object?>,
                    MiniMaxVideoGenerationResponse>(
                    httpClient,
                    endpoint,
                    miniMaxRequest,
                    DefaultJsonOptions,
                    cancellationToken);
                
                // Check for MiniMax error response
                if (response.BaseResp is { } baseResp && baseResp.StatusCode != 0)
                {
                    Logger.LogError("MiniMax video generation error: {StatusCode} - {StatusMsg}", 
                        baseResp.StatusCode, baseResp.StatusMsg);
                    throw new LLMCommunicationException($"MiniMax error: {baseResp.StatusMsg}");
                }

                if (string.IsNullOrEmpty(response.TaskId))
                {
                    // Should not reach here for video generation as it's always async
                    throw new LLMCommunicationException("MiniMax video generation did not return a task ID");
                }

                Logger.LogInformation("MiniMax video generation task created: {TaskId}", response.TaskId);

                var pollingTimeoutSeconds = ProviderTimeouts.VideoPollingSeconds();
                var initialDelay = TimeSpan.FromMilliseconds(2000);
                var maxDelay = TimeSpan.FromMilliseconds(30000);
                var approxMaxAttempts = (int)(pollingTimeoutSeconds / initialDelay.TotalSeconds);

                Logger.LogInformation("Configured video polling timeout: {TimeoutSeconds}s, approx max attempts: {MaxAttempts}",
                    pollingTimeoutSeconds, approxMaxAttempts);

                var options = new PollingOptions(
                    InitialDelay: initialDelay,
                    MaxDelay: maxDelay,
                    Timeout: TimeSpan.FromSeconds(pollingTimeoutSeconds),
                    Backoff: BackoffStrategy.ExponentialWithJitter,
                    MaxConsecutiveTransientErrors: 3,
                    BackoffMultiplier: 1.5,
                    JitterMilliseconds: 500);

                var taskId = response.TaskId;
                using var pollScope = BeginPollingScope("CreateVideo");
                var statusResult = await AsyncJobPoller.PollAsync(
                    fetchStatus: ct => FetchVideoStatusAsync(taskId, httpClient, ct),
                    classify: ClassifyVideoStatus,
                    extractSuccess: s => s,
                    extractFailure: s => new LLMCommunicationException(
                        $"MiniMax video generation failed: {s.BaseResp?.StatusMsg ?? "Unknown error"}"),
                    options: options,
                    logger: Logger,
                    cancellationToken: cancellationToken,
                    onProgress: async (poll, status) =>
                    {
                        if (_progressCallback is null || poll.State != JobState.InProgress)
                        {
                            return;
                        }
                        var pct = status.Status switch
                        {
                            "Preparing" => 10,
                            "Queueing" => 20,
                            "Pending" => 30,
                            "Processing" => CalculateProcessingProgress(poll.AttemptCount, approxMaxAttempts),
                            _ => 0,
                        };
                        await _progressCallback(taskId, status.Status ?? "unknown", pct);
                    },
                    operationName: $"MiniMax video generation {taskId}",
                    instrumentation: pollScope);

                Logger.LogInformation("MiniMax video generation completed: FileId={FileId}", statusResult.FileId);

                var videoUrl = $"{_baseUrl}/v1/files/retrieve?file_id={statusResult.FileId}";
                var videoData = new List<VideoData>
                {
                    new VideoData
                    {
                        Url = videoUrl,
                        Metadata = new VideoMetadata
                        {
                            Width = statusResult.VideoWidth,
                            Height = statusResult.VideoHeight,
                            Duration = request.Duration ?? 6,
                            Fps = request.Fps ?? 30,
                            Format = "mp4",
                            MimeType = "video/mp4",
                        },
                    },
                };

                if (request.ResponseFormat == "b64_json")
                {
                    try
                    {
                        Logger.LogInformation("Downloading video for base64 conversion: {Url}", statusResult.Video?.Url);
                        using var videoResponse = await httpClient.GetAsync(statusResult.Video?.Url ?? string.Empty, cancellationToken);
                        if (videoResponse.IsSuccessStatusCode)
                        {
                            var videoBytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                            videoData[0].B64Json = Convert.ToBase64String(videoBytes);
                            videoData[0].Url = null;
                            if (videoData[0].Metadata != null)
                            {
                                videoData[0].Metadata!.FileSizeBytes = videoBytes.Length;
                            }
                        }
                        else
                        {
                            Logger.LogWarning("Failed to download video from {Url}: {Status}",
                                statusResult.Video?.Url, videoResponse.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error downloading video from {Url}", statusResult.Video?.Url);
                    }
                }

                var videoDuration = statusResult.Video?.Duration ?? request.Duration ?? 6;

                return new VideoGenerationResponse
                {
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Data = videoData,
                    Model = request.Model ?? "video-01",
                    Usage = new VideoGenerationUsage
                    {
                        VideosGenerated = 1,
                        TotalDurationSeconds = videoDuration,
                        // MiniMax does not report cost; billing resolves it from the
                        // ModelCost configuration using duration/resolution, so no
                        // fabricated figure is surfaced here.
                        EstimatedCost = null,
                    },
                };
            }, "CreateVideo", cancellationToken);
        }

        private async Task<MiniMaxVideoStatusResponse> FetchVideoStatusAsync(
            string taskId,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            var endpoint = $"{_baseUrl}/v1/query/video_generation?task_id={taskId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            Logger.LogInformation("MiniMax video status check: {Status}", content);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Logger.LogWarning("Rate limited while checking video status, backing off");
                return new MiniMaxVideoStatusResponse
                {
                    BaseResp = new BaseResponse { StatusCode = 1013, StatusMsg = "HTTP 429 rate-limited" },
                };
            }
            if ((int)response.StatusCode >= 500)
            {
                Logger.LogWarning("Server error checking video status: {StatusCode} - {Response}",
                    response.StatusCode, content);
                throw new HttpRequestException(
                    $"Server error checking video status: {response.StatusCode}");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new LLMCommunicationException(
                    $"Client error checking video status: {response.StatusCode} - {content}");
            }

            MiniMaxVideoStatusResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(
                    content,
                    MiniMaxStreamJsonContext.Default.MiniMaxVideoStatusResponse);
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "Error deserializing status response: {Response}", content);
                throw new HttpRequestException("Failed to deserialize MiniMax video status response", ex);
            }
            if (parsed is null)
            {
                throw new HttpRequestException("MiniMax returned null video status");
            }
            return parsed;
        }

        private static JobState ClassifyVideoStatus(MiniMaxVideoStatusResponse status)
        {
            if (status.BaseResp is { } baseResp && baseResp.StatusCode != 0)
            {
                var errorMsg = $"MiniMax error {baseResp.StatusCode}: {baseResp.StatusMsg}";
                switch (baseResp.StatusCode)
                {
                    case 1002:
                    case 1004:
                        throw new UnauthorizedAccessException(errorMsg);

                    case 1008:
                    case 1013:
                        return JobState.RateLimited;

                    case 2013:
                        throw new LLMCommunicationException($"Content policy violation: {baseResp.StatusMsg}");

                    default:
                        if (baseResp.StatusCode >= 2000)
                        {
                            throw new LLMCommunicationException(errorMsg);
                        }
                        return JobState.TransientError;
                }
            }

            return status.Status switch
            {
                "Success" when !string.IsNullOrEmpty(status.FileId) => JobState.Succeeded,
                "Failed" => JobState.Failed,
                _ => JobState.InProgress,
            };
        }

        /// <summary>
        /// Calculates progress percentage for processing status based on polling attempts.
        /// </summary>
        private static int CalculateProcessingProgress(int currentAttempt, int maxAttempts)
        {
            // Processing starts at 30% and goes up to 90%
            const int minProgress = 30;
            const int maxProgress = 90;
            const int startSlowdownAttempt = 10; // Start slowing down after 10 attempts
            
            if (currentAttempt < startSlowdownAttempt)
            {
                // Linear progress for first attempts
                var linearProgress = (double)currentAttempt / startSlowdownAttempt;
                return minProgress + (int)((maxProgress - minProgress) * linearProgress * 0.7); // Use 70% of range
            }
            else
            {
                // Logarithmic progress for later attempts
                var remainingAttempts = currentAttempt - startSlowdownAttempt;
                var remainingMaxAttempts = Math.Max(1, maxAttempts - startSlowdownAttempt);
                var logProgress = Math.Log(remainingAttempts + 1) / Math.Log(remainingMaxAttempts + 1);
                var baseProgress = minProgress + (int)((maxProgress - minProgress) * 0.7);
                return baseProgress + (int)((maxProgress - baseProgress) * logProgress);
            }
        }

        /// <summary>
        /// Creates a configured HttpClient specifically for video generation requests.
        /// This client has no timeout policy to support long-running video generation.
        /// </summary>
        /// <param name="apiKey">Optional API key to override the one in credentials.</param>
        /// <returns>A configured HttpClient instance for video generation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when IHttpClientFactory is not available.</exception>
        protected virtual HttpClient CreateVideoHttpClient(string? apiKey = null)
        {
            if (HttpClientFactory == null)
            {
                throw new InvalidOperationException(
                    $"IHttpClientFactory is required for {ProviderName} video client but was not injected. " +
                    "Ensure IHttpClientFactory is registered in the dependency injection container. " +
                    "Creating HttpClient instances directly can cause socket exhaustion under load.");
            }

            // Use a dedicated named client for video operations (should be configured without aggressive timeout policies)
            var client = HttpClientFactory.CreateClient($"{ProviderName}VideoClient");

            string effectiveApiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : PrimaryKeyCredential.ApiKey!;
            if (string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                throw new ConfigurationException($"API key is missing for provider '{ProviderName}'");
            }

            // Configure headers manually to avoid any base class behavior
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("User-Agent", "ConduitLLM");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", effectiveApiKey);

            // Use large file download timeout for video files
            client.Timeout = LargeFileDownloadTimeout;

            Logger.LogInformation("Created video HTTP client with {Timeout} timeout via IHttpClientFactory",
                LargeFileDownloadTimeout);

            return client;
        }
    }
}
