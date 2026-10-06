using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Decorators
{
    /// <summary>
    /// Decorator that sets provider key context and tracks errors for LLM operations
    /// </summary>
    public class ContextAwareLLMClient :
        ILLMClient,
        ILLMClientDecorator,
        IVideoGenerationClient,
        IAuthenticationVerifiable
    {
        private readonly ILLMClient _innerClient;
        private readonly int _keyId;
        private readonly int _providerId;
        private readonly string? _providerName;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ContextAwareLLMClient>? _logger;

        public ContextAwareLLMClient(
            ILLMClient innerClient,
            int keyId,
            int providerId,
            IServiceProvider serviceProvider,
            string? providerName = null)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _keyId = keyId;
            _providerId = providerId;
            _providerName = providerName;
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = serviceProvider.GetService<ILogger<ContextAwareLLMClient>>();
        }

        /// <inheritdoc />
        public ILLMClient InnerClient => _innerClient;

        public async Task<ChatCompletionResponse> CreateChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await TrackedAsync(
                () => _innerClient.CreateChatCompletionAsync(request, apiKey, cancellationToken));
        }

        public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using (ProviderKeyContext.Set(_keyId, _providerId))
            {
                var sourceStream = _innerClient.StreamChatCompletionAsync(request, apiKey, cancellationToken);
                var enumerator = sourceStream.GetAsyncEnumerator(cancellationToken);
                bool errorTracked = false;
                
                try
                {
                    while (true)
                    {
                        ChatCompletionChunk current;
                        bool hasNext;
                        
                        try
                        {
                            hasNext = await enumerator.MoveNextAsync();
                            if (!hasNext) break;
                            current = enumerator.Current;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(
                                "Caught exception in streaming: Type={ExceptionType}, Message={Message}, HasStatusCode={HasStatusCode}",
                                ex.GetType().Name, ex.Message.Substring(0, Math.Min(ex.Message.Length, 200)), (ex as LLMCommunicationException)?.StatusCode);

                            StampProviderName(ex);

                            // Track error only once per stream
                            if (!errorTracked)
                            {
                                // Try to extract LLMCommunicationException from nested exceptions
                                var llmEx = LLMCommunicationException.FindWithStatus(ex);
                                if (llmEx != null)
                                {
                                    _logger?.LogWarning(
                                        "Extracted LLMCommunicationException: StatusCode={StatusCode}",
                                        llmEx.StatusCode);
                                    await TrackErrorAsync(llmEx);
                                    errorTracked = true;
                                }
                                else
                                {
                                    _logger?.LogWarning("Could not extract LLMCommunicationException from {ExceptionType}", ex.GetType().Name);
                                }
                            }
                            throw;
                        }
                        
                        yield return current;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }
            }
        }

        public async Task<List<string>> ListModelsAsync(
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await TrackedAsync(() => _innerClient.ListModelsAsync(apiKey, cancellationToken));
        }

        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await TrackedAsync(
                () => _innerClient.CreateEmbeddingAsync(request, apiKey, cancellationToken));
        }

        public async Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await TrackedAsync(
                () => _innerClient.CreateImageAsync(request, apiKey, cancellationToken));
        }

        public async Task<VideoGenerationResponse> CreateVideoAsync(
            VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await TrackedAsync(async () =>
            {
                var videoClient = _innerClient.FindInChain<IVideoGenerationClient>()
                    ?? throw new NotSupportedException(
                        $"The underlying client {_innerClient.GetType().Name} does not support video generation");

                return await videoClient.CreateVideoAsync(request, apiKey, cancellationToken);
            });
        }

        /// <summary>
        /// Verifies authentication by delegating to the inner client if it supports
        /// <see cref="IAuthenticationVerifiable"/>.
        /// </summary>
        public Task<AuthenticationResult> VerifyAuthenticationAsync(
            string? apiKey = null,
            string? baseUrl = null,
            CancellationToken cancellationToken = default)
        {
            return Utilities.AuthenticationVerificationDelegator.VerifyAsync(
                _innerClient,
                _innerClient.GetType().Name,
                apiKey,
                baseUrl,
                cancellationToken);
        }

        /// <summary>
        /// Gets the health check URL by delegating to the inner client if it supports
        /// <see cref="IAuthenticationVerifiable"/>.
        /// </summary>
        public string GetHealthCheckUrl(string? baseUrl = null)
        {
            return Utilities.AuthenticationVerificationDelegator.GetHealthCheckUrl(_innerClient, baseUrl);
        }

        /// <summary>
        /// Stamps this client's provider name on every <see cref="LLMCommunicationException"/>
        /// in the chain so customer-facing translation (Internal mode) can name the provider.
        /// Never overwrites a name set closer to the source.
        /// </summary>
        private void StampProviderName(Exception exception)
        {
            if (string.IsNullOrWhiteSpace(_providerName))
            {
                return;
            }

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is LLMCommunicationException communicationException)
                {
                    communicationException.ProviderName ??= _providerName;
                }
            }
        }

        private async Task<T> TrackedAsync<T>(Func<Task<T>> operation)
        {
            using (ProviderKeyContext.Set(_keyId, _providerId))
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex)
                {
                    StampProviderName(ex);

                    var communicationException = LLMCommunicationException.FindWithStatus(ex);
                    if (communicationException is not null)
                    {
                        await TrackErrorAsync(communicationException);
                    }

                    throw;
                }
            }
        }

        private async Task TrackErrorAsync(LLMCommunicationException ex)
        {
            try
            {
                // Skip tracking for test keys (ID 0 or negative)
                if (_keyId <= 0)
                {
                    return;
                }

                var errorTracker = _serviceProvider.GetService<IProviderErrorTrackingService>();
                if (errorTracker == null)
                {
                    _logger?.LogDebug("Error tracking service not available");
                    return;
                }

                var errorType = ProviderErrorClassifier.ClassifyException(ex);
                
                // Only track errors that are meaningful for provider health
                if (!ProviderErrorClassifier.ShouldTrack(errorType))
                {
                    return;
                }

                await errorTracker.TrackErrorAsync(new ProviderErrorInfo
                {
                    KeyCredentialId = _keyId,
                    ProviderId = _providerId,
                    ErrorType = errorType,
                    ErrorMessage = ex.Message,
                    HttpStatusCode = (int?)ex.StatusCode,
                    RetryAttempt = 0, // Direct error, not from retry
                    RequestId = _serviceProvider
                        .GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()
                        ?.HttpContext?.TraceIdentifier
                });

                _logger?.LogInformation(
                    "Tracked {ErrorType} error for key {KeyId}, provider {ProviderId}",
                    errorType, _keyId, _providerId);
            }
            catch (Exception trackingEx)
            {
                // Don't let error tracking break the main flow
                _logger?.LogError(trackingEx, "Failed to track provider error");
            }
        }

    }
}
