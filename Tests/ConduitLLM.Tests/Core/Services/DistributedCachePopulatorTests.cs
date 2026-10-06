using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Core.Services;

public sealed class DistributedCachePopulatorTests
{
    [Fact]
    public async Task FactoryFailure_IsNotExecutedTwiceAfterOptionalFallback()
    {
        var locks = new Mock<IDistributedLockProvider>();
        locks.Setup(value => value.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backend"));
        var populator = new DistributedCachePopulator(locks.Object, Mock.Of<ILogger<DistributedCachePopulator>>());
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => populator.GetOrPopulateAsync<string>("work",
            () => Task.FromResult<string>(null), () => { calls++; throw new InvalidOperationException("factory"); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CanceledCacheRead_DoesNotRunFactoryFallback()
    {
        var locks = new Mock<IDistributedLockProvider>();
        var populator = new DistributedCachePopulator(locks.Object, Mock.Of<ILogger<DistributedCachePopulator>>());
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => populator.GetOrPopulateAsync<string>("work",
            () => Task.FromCanceled<string>(new CancellationToken(true)), () => { calls++; return Task.FromResult("value"); }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GetOrPopulateAsync_ConcurrentMisses_RunFactoryOncePerKey()
    {
        var distributedLocks = new Mock<IDistributedLockProvider>();
        distributedLocks
            .Setup(service => service.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDistributedLockOwnership)null!);
        var populator = new DistributedCachePopulator(
            distributedLocks.Object,
            Mock.Of<ILogger<DistributedCachePopulator>>());
        string? cachedValue = null;
        var factoryCalls = 0;

        var requests = Enumerable.Range(0, 50).Select(_ => populator.GetOrPopulateAsync(
            "contended-key",
            () => Task.FromResult(cachedValue),
            async () =>
            {
                Interlocked.Increment(ref factoryCalls);
                await Task.Delay(20);
                cachedValue = "value";
                return cachedValue;
            }));

        var results = await Task.WhenAll(requests);

        Assert.Equal(1, factoryCalls);
        Assert.All(results, result => Assert.Equal("value", result));
    }
}
