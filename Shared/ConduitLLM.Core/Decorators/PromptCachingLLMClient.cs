using System.Runtime.CompilerServices;
using System.Text.Json;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Utilities;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Decorators;

/// <summary>
/// Decorator that automatically injects cache_control directives into chat completion
/// requests when prompt caching auto-injection is enabled via GlobalSettings.
/// </summary>
public class PromptCachingLLMClient :
    ILLMClient,
    ILLMClientDecorator,
    IVideoGenerationClient,
    IAuthenticationVerifiable
{
    private readonly ILLMClient _innerClient;
    private readonly IGlobalSettingsCacheService _settingsService;
    private readonly ILogger<PromptCachingLLMClient> _logger;
    private readonly string _provider;
    private readonly string _providerModelId;

    /// <summary>
    /// GlobalSettings key for the prompt caching configuration.
    /// </summary>
    public const string SettingsKey = PromptCachingConstants.SettingsKey;

    public PromptCachingLLMClient(
        ILLMClient innerClient,
        IGlobalSettingsCacheService settingsService,
        ILogger<PromptCachingLLMClient> logger,
        string provider = "unknown",
        string providerModelId = "unknown")
    {
        _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _provider = provider;
        _providerModelId = providerModelId;
    }

    /// <inheritdoc />
    public ILLMClient InnerClient => _innerClient;

    /// <inheritdoc />
    public async Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        await TryInjectCacheControlAsync(request);
        return await _innerClient.CreateChatCompletionAsync(request, apiKey, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        string? apiKey = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await TryInjectCacheControlAsync(request);
        await foreach (var chunk in _innerClient.StreamChatCompletionAsync(request, apiKey, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public Task<List<string>> ListModelsAsync(string? apiKey = null, CancellationToken cancellationToken = default)
        => _innerClient.ListModelsAsync(apiKey, cancellationToken);

    /// <inheritdoc />
    public Task<EmbeddingResponse> CreateEmbeddingAsync(EmbeddingRequest request, string? apiKey = null, CancellationToken cancellationToken = default)
        => _innerClient.CreateEmbeddingAsync(request, apiKey, cancellationToken);

    /// <inheritdoc />
    public Task<ImageGenerationResponse> CreateImageAsync(ImageGenerationRequest request, string? apiKey = null, CancellationToken cancellationToken = default)
        => _innerClient.CreateImageAsync(request, apiKey, cancellationToken);

    /// <summary>
    /// Generates video by forwarding the optional provider capability through this decorator.
    /// </summary>
    public Task<VideoGenerationResponse> CreateVideoAsync(
        VideoGenerationRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => (_innerClient.FindInChain<IVideoGenerationClient>()
            ?? throw new NotSupportedException(
                $"The underlying client {_innerClient.GetType().Name} does not support video generation"))
            .CreateVideoAsync(request, apiKey, cancellationToken);

    /// <summary>
    /// Verifies authentication by delegating to the inner client if it supports
    /// <see cref="IAuthenticationVerifiable"/>.
    /// </summary>
    public Task<AuthenticationResult> VerifyAuthenticationAsync(
        string? apiKey = null,
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        return AuthenticationVerificationDelegator.VerifyAsync(
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
        return AuthenticationVerificationDelegator.GetHealthCheckUrl(_innerClient, baseUrl);
    }

    private async Task TryInjectCacheControlAsync(ChatCompletionRequest request)
    {
        try
        {
            var config = await GetPromptCachingConfigAsync();
            if (config is not null)
            {
                config = PromptCachingPolicyResolver.Migrate(config);
                var errors = PromptCachingPolicyResolver.Validate(config);
                if (errors.Count != 0)
                {
                    PromptCachingInjectionMetrics.RecordError(request.Model ?? "unknown");
                    _logger.LogWarning("Ignoring invalid prompt caching configuration: {Errors}", string.Join("; ", errors));
                    return;
                }

                request.PromptCachingIntent = PromptCachingPolicyResolver.Resolve(
                    config, _provider, _providerModelId, request.RoutingAffinityKey);
                if (request.PromptCachingIntent is not null)
                {
                    PromptCachingInjectionMetrics.RecordSuccess(request.Model ?? "unknown");
                    _logger.LogDebug("Resolved {Strategy} prompt caching for {Provider}/{Model}",
                        request.PromptCachingIntent.Strategy, _provider, _providerModelId);
                }
            }
        }
        catch (Exception ex)
        {
            // Don't fail the request if cache injection fails — just log and continue
            PromptCachingInjectionMetrics.RecordError(request.Model ?? "unknown");
            _logger.LogWarning(ex, "Failed to inject cache_control directives, continuing without caching");
        }
    }

    private async Task<PromptCachingConfig?> GetPromptCachingConfigAsync()
    {
        var json = await _settingsService.GetSettingValueAsync(SettingsKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var context = new CoreInternalJsonContext(
            new JsonSerializerOptions(PromptCachingSerialization.Options));
        return JsonSerializer.Deserialize(json, context.PromptCachingConfig);
    }
}
