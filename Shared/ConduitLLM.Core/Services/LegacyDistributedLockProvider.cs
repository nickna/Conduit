using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Core.Services;

/// <summary>Temporary migration bridge; retains old leases. Remove after DL-6 gates, before delivery.</summary>
public sealed class LegacyDistributedLockProvider(IDistributedLockService legacy) : IDistributedLockProvider
{
    public async Task<IDistributedLockOwnership?> TryAcquireAsync(
        string key, TimeSpan acquisitionTimeout = default, CancellationToken cancellationToken = default)
    {
        try
        {
            var handle = acquisitionTimeout == TimeSpan.Zero
                ? await legacy.AcquireLockAsync(key, TimeSpan.FromMinutes(30), cancellationToken)
                : await legacy.AcquireLockWithRetryAsync(key, TimeSpan.FromMinutes(30), acquisitionTimeout,
                    TimeSpan.FromMilliseconds(50), cancellationToken);
            return handle is null ? null : new Ownership(handle);
        }
        catch (TimeoutException) { return null; }
    }

    private sealed class Ownership(IDistributedLock handle) : IDistributedLockOwnership
    {
        public CancellationToken HandleLostToken => CancellationToken.None;
        public ValueTask DisposeAsync() => handle.DisposeAsync();
    }
}
