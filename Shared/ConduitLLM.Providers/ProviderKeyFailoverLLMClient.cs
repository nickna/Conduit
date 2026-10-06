using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers;

/// <summary>
/// Retries fatal provider failures against another enabled key for the same provider.
/// Key-specific clients are expected to include <c>ContextAwareLLMClient</c>, so a
/// failed attempt is tracked and disabled before the next target is invoked.
/// </summary>
internal sealed class ProviderKeyFailoverLLMClient :
    ILLMClient,
    ILLMClientDecorator,
    IVideoGenerationClient,
    IAuthenticationVerifiable
{
    private const int MaxAttempts = 3;

    private readonly IReadOnlyList<ProviderKeyFailoverTarget> _targets;
    private readonly ILogger? _logger;

    public ProviderKeyFailoverLLMClient(
        IReadOnlyList<ProviderKeyFailoverTarget> targets,
        ILogger? logger = null)
    {
        if (targets is null || targets.Count == 0)
        {
            throw new ArgumentException("At least one provider key target is required.", nameof(targets));
        }

        _targets = targets;
        _logger = logger;
    }

    public ILLMClient InnerClient => _targets[0].Client;

    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            client => client.CreateChatCompletionAsync(request, apiKey, cancellationToken),
            allowFailover: string.IsNullOrWhiteSpace(apiKey));

    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        string? apiKey = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ExceptionDispatchInfo? originalError = null;
        var excludedGroups = new HashSet<int>();
        var attemptCount = 0;

        foreach (var target in _targets)
        {
            if (attemptCount >= MaxAttempts ||
                (target.ProviderAccountGroup > 0 && excludedGroups.Contains(target.ProviderAccountGroup)))
            {
                continue;
            }

            attemptCount++;
            var enumerator = target.Client
                .StreamChatCompletionAsync(request, apiKey, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception exception)
            {
                await enumerator.DisposeAsync();
                var fatalKind = ClassifyFatalError(exception);
                if (!string.IsNullOrWhiteSpace(apiKey) || fatalKind == FatalErrorKind.None)
                {
                    throw;
                }

                originalError ??= ExceptionDispatchInfo.Capture(exception);
                ExcludeAccountGroupIfNeeded(target, fatalKind, excludedGroups);
                LogFailover(target, fatalKind, attemptCount);
                continue;
            }

            if (!hasFirst)
            {
                await enumerator.DisposeAsync();
                yield break;
            }

            try
            {
                // Once any chunk is visible to the caller, retrying could duplicate output.
                yield return enumerator.Current;
                while (await enumerator.MoveNextAsync())
                {
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            yield break;
        }

        ThrowOriginalOrNoEligibleTarget(originalError);
    }

    public Task<List<string>> ListModelsAsync(
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            client => client.ListModelsAsync(apiKey, cancellationToken),
            allowFailover: string.IsNullOrWhiteSpace(apiKey));

    public Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            client => client.CreateEmbeddingAsync(request, apiKey, cancellationToken),
            allowFailover: string.IsNullOrWhiteSpace(apiKey));

    public Task<ImageGenerationResponse> CreateImageAsync(
        ImageGenerationRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            client => client.CreateImageAsync(request, apiKey, cancellationToken),
            allowFailover: string.IsNullOrWhiteSpace(apiKey));

    public Task<VideoGenerationResponse> CreateVideoAsync(
        VideoGenerationRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            client => InvokeVideoAsync(client, request, apiKey, cancellationToken),
            allowFailover: string.IsNullOrWhiteSpace(apiKey));

    public Task<AuthenticationResult> VerifyAuthenticationAsync(
        string? apiKey = null,
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        return AuthenticationVerificationDelegator.VerifyAsync(
            InnerClient,
            InnerClient.GetType().Name,
            apiKey,
            baseUrl,
            cancellationToken);
    }

    public string GetHealthCheckUrl(string? baseUrl = null)
    {
        return AuthenticationVerificationDelegator.GetHealthCheckUrl(InnerClient, baseUrl);
    }

    private async Task<T> ExecuteAsync<T>(
        Func<ILLMClient, Task<T>> operation,
        bool allowFailover)
    {
        ExceptionDispatchInfo? originalError = null;
        var excludedGroups = new HashSet<int>();
        var attemptCount = 0;

        foreach (var target in _targets)
        {
            if (attemptCount >= MaxAttempts ||
                (target.ProviderAccountGroup > 0 && excludedGroups.Contains(target.ProviderAccountGroup)))
            {
                continue;
            }

            attemptCount++;
            try
            {
                return await operation(target.Client);
            }
            catch (Exception exception)
            {
                var fatalKind = ClassifyFatalError(exception);
                if (!allowFailover || fatalKind == FatalErrorKind.None)
                {
                    throw;
                }

                originalError ??= ExceptionDispatchInfo.Capture(exception);
                ExcludeAccountGroupIfNeeded(target, fatalKind, excludedGroups);
                LogFailover(target, fatalKind, attemptCount);
            }
        }

        ThrowOriginalOrNoEligibleTarget(originalError);
        throw new InvalidOperationException("Unreachable.");
    }

    private void LogFailover(
        ProviderKeyFailoverTarget target,
        FatalErrorKind fatalKind,
        int attemptCount)
    {
        _logger?.LogWarning(
            "Provider key {KeyId} failed with {FatalErrorKind}; trying another key after attempt {Attempt}/{MaxAttempts}",
            target.KeyId,
            fatalKind,
            attemptCount,
            MaxAttempts);
    }

    private static void ExcludeAccountGroupIfNeeded(
        ProviderKeyFailoverTarget target,
        FatalErrorKind fatalKind,
        ISet<int> excludedGroups)
    {
        if (fatalKind == FatalErrorKind.InsufficientBalance &&
            target.ProviderAccountGroup > 0)
        {
            excludedGroups.Add(target.ProviderAccountGroup);
        }
    }

    private static FatalErrorKind ClassifyFatalError(Exception exception)
    {
        var communicationException = LLMCommunicationException.FindWithStatus(exception);
        if (communicationException is null)
        {
            return FatalErrorKind.None;
        }

        var errorType = ProviderErrorClassifier.ClassifyException(exception);

        return errorType switch
        {
            ProviderErrorType.InvalidApiKey => FatalErrorKind.Credential,
            ProviderErrorType.InsufficientBalance => FatalErrorKind.InsufficientBalance,
            ProviderErrorType.AccessForbidden => FatalErrorKind.Credential,
            _ => FatalErrorKind.None
        };
    }

    private static Task<VideoGenerationResponse> InvokeVideoAsync(
        ILLMClient client,
        VideoGenerationRequest request,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var videoClient = client.FindInChain<IVideoGenerationClient>()
            ?? throw new NotSupportedException(
                $"The underlying client {client.GetType().Name} does not support video generation");

        return videoClient.CreateVideoAsync(request, apiKey, cancellationToken);
    }

    private static void ThrowOriginalOrNoEligibleTarget(ExceptionDispatchInfo? originalError)
    {
        if (originalError is not null)
        {
            originalError.Throw();
        }

        throw new InvalidOperationException("No eligible provider key was available for failover.");
    }

    private enum FatalErrorKind
    {
        None,
        Credential,
        InsufficientBalance
    }
}

internal sealed class ProviderKeyFailoverTarget
{
    private readonly Lazy<ILLMClient> _client;

    public ProviderKeyFailoverTarget(
        int keyId,
        int providerAccountGroup,
        Func<ILLMClient> clientFactory)
    {
        KeyId = keyId;
        ProviderAccountGroup = providerAccountGroup;
        _client = new Lazy<ILLMClient>(
            clientFactory ?? throw new ArgumentNullException(nameof(clientFactory)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int KeyId { get; }
    public int ProviderAccountGroup { get; }
    public ILLMClient Client => _client.Value;
}
