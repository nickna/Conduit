using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>Striped local coordination plus optional distributed ownership for independent caches.</summary>
public sealed class DistributedCachePopulator : IDistributedCachePopulator
{
    private readonly IDistributedLockProvider _lockService;
    private readonly ILogger<DistributedCachePopulator> _logger;
    private readonly StripedAsyncLock _localLocks = new();
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public DistributedCachePopulator(IDistributedLockProvider lockService, ILogger<DistributedCachePopulator> logger)
    {
        _lockService = lockService;
        _logger = logger;
    }

    // Existing uncancellable callbacks are awaited in full under healthy ownership.
    public Task<T?> GetOrPopulateAsync<T>(string lockKey, Func<Task<T?>> cacheCheck,
        Func<Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
        => GetOrPopulateAsync(lockKey, _ => cacheCheck(), _ => factory(), cancellationToken);

    public async Task<T?> GetOrPopulateAsync<T>(string lockKey, Func<CancellationToken, Task<T?>> cacheCheck,
        Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
    {
        async Task<T?> CheckAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var value = await cacheCheck(token);
                token.ThrowIfCancellationRequested();
                return value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                _logger.LogWarning(ex, "Cache check failed for {LockKey}; proceeding to population", lockKey);
                return null;
            }
        }
        async Task<T?> LoadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var value = await factory(token);
            token.ThrowIfCancellationRequested();
            return value;
        }

        var cached = await CheckAsync(cancellationToken);
        if (cached is not null) { return cached; }
        IDisposable? localLock;
        try { localLock = await _localLocks.TryAcquireAsync(lockKey, LockTimeout, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local cache coordination failed for {LockKey}; falling back to factory", lockKey);
            return await LoadAsync(cancellationToken);
        }
        if (localLock is null)
        {
            _logger.LogWarning("Local cache coordination timed out for {LockKey}; falling back to factory", lockKey);
            return await LoadAsync(cancellationToken);
        }
        using (localLock)
        {
            cached = await CheckAsync(cancellationToken);
            if (cached is not null) { return cached; }
            var result = await _lockService.RunWithOptionalLockAsync(lockKey, LockTimeout,
                async (acquired, token) =>
                {
                    if (acquired)
                    {
                        var value = await CheckAsync(token);
                        if (value is not null) { return value; }
                    }
                    return await LoadAsync(token);
                }, _logger, cancellationToken);
            return result.Value;
        }
    }
}
