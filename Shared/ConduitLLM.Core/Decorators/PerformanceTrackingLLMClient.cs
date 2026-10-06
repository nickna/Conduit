using System.Diagnostics;
using System.Runtime.CompilerServices;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Decorators
{
    /// <summary>
    /// Decorator that adds performance tracking to LLM client operations.
    /// </summary>
    public class PerformanceTrackingLLMClient :
        ILLMClient,
        ILLMClientDecorator,
        IVideoGenerationClient,
        IAuthenticationVerifiable
    {
        private readonly ILLMClient _innerClient;
        private readonly IPerformanceMetricsService _metricsService;
        private readonly ILogger<PerformanceTrackingLLMClient> _logger;
        private readonly bool _isEnabled;
        private readonly string _providerName;

        public PerformanceTrackingLLMClient(
            ILLMClient innerClient,
            IPerformanceMetricsService metricsService,
            ILogger<PerformanceTrackingLLMClient> logger,
            string providerName,
            bool isEnabled = true)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _providerName = providerName ?? throw new ArgumentNullException(nameof(providerName));
            _isEnabled = isEnabled;
        }

        /// <inheritdoc />
        public ILLMClient InnerClient => _innerClient;


        /// <summary>
        /// Creates a chat completion with performance tracking.
        /// </summary>
        public async Task<ChatCompletionResponse> CreateChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            if (!_isEnabled)
            {
                return await _innerClient.CreateChatCompletionAsync(request, apiKey, cancellationToken);
            }

            var stopwatch = Stopwatch.StartNew();
            var retryAttempts = 0;

            try
            {
                var response = await _innerClient.CreateChatCompletionAsync(request, apiKey, cancellationToken);
                stopwatch.Stop();

                // Calculate and inject performance metrics
                response.PerformanceMetrics = _metricsService.CalculateMetrics(
                    response,
                    stopwatch.Elapsed,
                    _providerName,
                    request.Model,
                    streaming: false,
                    retryAttempts: retryAttempts);

                _logger.LogDebug(
                    "Chat completion for model {Model} completed in {ElapsedMs}ms with {TokensPerSecond:F2} tokens/sec",
                    request.Model,
                    stopwatch.ElapsedMilliseconds,
                    response.PerformanceMetrics.TokensPerSecond ?? 0);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, 
                    "Chat completion for model {Model} failed after {ElapsedMs}ms",
                    request.Model,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Streams a chat completion with performance tracking.
        /// </summary>
        public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!_isEnabled)
            {
                await foreach (var chunk in _innerClient.StreamChatCompletionAsync(request, apiKey, cancellationToken))
                {
                    yield return chunk;
                }
                yield break;
            }

            var tracker = _metricsService.CreateStreamingTracker(_providerName, request.Model);
            var isFirstChunk = true;
            Usage? finalUsage = null;

            await foreach (var chunk in _innerClient.StreamChatCompletionAsync(request, apiKey, cancellationToken))
            {
                if (chunk != null)
                {
                    if (isFirstChunk)
                    {
                        tracker.RecordFirstToken();
                        isFirstChunk = false;
                    }
                    else if (chunk.Choices?.Any(c => !string.IsNullOrEmpty(c.Delta?.Content)) == true)
                    {
                        tracker.RecordToken();
                    }

                    // Some providers include usage in the final chunk
                    if (chunk.Choices?.Any(c => c.FinishReason != null) == true)
                    {
                        // This might be the final chunk - check for usage data
                        // Note: This would need to be extended based on provider-specific behavior
                    }
                }

                yield return chunk!;
            }

            // Log streaming performance metrics
            var metrics = tracker.GetMetrics(finalUsage);
            _logger.LogDebug(
                "Streaming completion for model {Model} - TTFT: {TimeToFirstToken}ms, Avg tokens/sec: {TokensPerSecond:F2}",
                request.Model,
                metrics.TimeToFirstTokenMs ?? 0,
                metrics.TokensPerSecond ?? 0);
        }

        /// <summary>
        /// Creates embeddings with performance tracking.
        /// </summary>
        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            if (!_isEnabled)
            {
                return await _innerClient.CreateEmbeddingAsync(request, apiKey, cancellationToken);
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var response = await _innerClient.CreateEmbeddingAsync(request, apiKey, cancellationToken);
                stopwatch.Stop();

                _logger.LogDebug(
                    "Embedding creation for model {Model} completed in {ElapsedMs}ms",
                    request.Model,
                    stopwatch.ElapsedMilliseconds);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex,
                    "Embedding creation for model {Model} failed after {ElapsedMs}ms",
                    request.Model,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Generates images with performance tracking.
        /// </summary>
        public async Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            if (!_isEnabled)
            {
                return await _innerClient.CreateImageAsync(request, apiKey, cancellationToken);
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var response = await _innerClient.CreateImageAsync(request, apiKey, cancellationToken);
                stopwatch.Stop();

                _logger.LogDebug(
                    "Image generation for model {Model} completed in {ElapsedMs}ms",
                    request.Model,
                    stopwatch.ElapsedMilliseconds);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex,
                    "Image generation for model {Model} failed after {ElapsedMs}ms",
                    request.Model,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Generates video by forwarding the optional provider capability through this decorator.
        /// </summary>
        public async Task<VideoGenerationResponse> CreateVideoAsync(
            VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            var videoClient = _innerClient.FindInChain<IVideoGenerationClient>()
                ?? throw new NotSupportedException(
                    $"The underlying client {_innerClient.GetType().Name} does not support video generation");

            return await videoClient.CreateVideoAsync(request, apiKey, cancellationToken);
        }

        /// <summary>
        /// Lists available models.
        /// </summary>
        public Task<List<string>> ListModelsAsync(string? apiKey = null, CancellationToken cancellationToken = default)
        {
            // No performance tracking needed for model listing
            return _innerClient.ListModelsAsync(apiKey, cancellationToken);
        }

        /// <summary>
        /// Verifies authentication by delegating to the inner client if it supports IAuthenticationVerifiable.
        /// </summary>
        public Task<AuthenticationResult> VerifyAuthenticationAsync(
            string? apiKey = null, 
            string? baseUrl = null,
            CancellationToken cancellationToken = default)
        {
            return Utilities.AuthenticationVerificationDelegator.VerifyAsync(
                _innerClient,
                _providerName,
                apiKey,
                baseUrl,
                cancellationToken);
        }

        /// <summary>
        /// Gets the health check URL by delegating to the inner client if it supports IAuthenticationVerifiable.
        /// </summary>
        public string GetHealthCheckUrl(string? baseUrl = null)
        {
            return Utilities.AuthenticationVerificationDelegator.GetHealthCheckUrl(_innerClient, baseUrl);
        }
    }
}
