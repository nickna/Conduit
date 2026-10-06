using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Extensions;

public readonly record struct OptionalLockResult<T>(bool Executed, T? Value);

public static class DistributedLockExtensions
{
    /// <summary>Apply optional acquisition policy; an operation is invoked at most once.</summary>
    public static async Task<OptionalLockResult<T>> RunWithOptionalLockAsync<T>(
        this IDistributedLockProvider? lockService, string lockKey, TimeSpan acquisitionTimeout,
        Func<bool, CancellationToken, Task<T>> operation, ILogger logger,
        CancellationToken cancellationToken = default, bool skipOnTimeout = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDistributedLockOwnership? ownership = null;
        if (lockService is not null)
        {
            try
            {
                ownership = await lockService.TryAcquireAsync(lockKey, acquisitionTimeout, cancellationToken);
                if (ownership is null && skipOnTimeout) { return new(false, default); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to acquire distributed lock {LockKey}; proceeding without coordination", lockKey);
            }
        }
        else
        {
            logger.LogWarning("Distributed lock service is unavailable for {LockKey}; proceeding without coordination", lockKey);
        }

        try
        {
            using var work = ownership?.CreateOperationCancellation(cancellationToken);
            var protectedToken = work?.Token ?? cancellationToken;
            protectedToken.ThrowIfCancellationRequested();
            var value = await operation(ownership is not null, protectedToken);
            protectedToken.ThrowIfCancellationRequested();
            return new(true, value);
        }
        finally
        {
            if (ownership is not null)
            {
                try { await ownership.DisposeAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Error releasing distributed lock {LockKey}", lockKey); }
            }
        }
    }
}
