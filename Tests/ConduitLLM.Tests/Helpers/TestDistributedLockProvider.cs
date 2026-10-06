using System.Collections.Concurrent;
using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Tests.Helpers;

/// <summary>Test-only session ownership: no expiry timer or production backend fallback.</summary>
internal sealed class TestDistributedLockProvider : IDistributedLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public async Task<IDistributedLockOwnership?> TryAcquireAsync(
        string key, TimeSpan acquisitionTimeout = default, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1));
        return await semaphore.WaitAsync(acquisitionTimeout, cancellationToken) ? new Ownership(semaphore) : null;
    }

    private sealed class Ownership(SemaphoreSlim semaphore) : IDistributedLockOwnership
    {
        private int _disposed;
        public CancellationToken HandleLostToken => CancellationToken.None;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) { semaphore.Release(); }
            return ValueTask.CompletedTask;
        }
    }
}
