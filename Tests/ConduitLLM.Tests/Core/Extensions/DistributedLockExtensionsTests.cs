using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Core.Extensions;

public sealed class DistributedLockExtensionsTests
{
    [Fact]
    public async Task AcquiredOwnership_IsReleasedAsynchronously()
    {
        var ownership = new Mock<IDistributedLockOwnership>();
        var provider = new Mock<IDistributedLockProvider>();
        provider.Setup(value => value.TryAcquireAsync("work", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(ownership.Object);
        var result = await provider.Object.RunWithOptionalLockAsync("work", TimeSpan.FromSeconds(1),
            (acquired, token) => Task.FromResult(acquired && token.CanBeCanceled ? 42 : 0), Mock.Of<ILogger>());
        Assert.True(result.Executed);
        Assert.Equal(42, result.Value);
        ownership.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("absent", true, true)]
    [InlineData("absent", false, true)]
    [InlineData("busy", true, false)]
    [InlineData("busy", false, true)]
    [InlineData("backend", true, true)]
    [InlineData("backend", false, true)]
    [InlineData("backend-timeout", true, true)]
    public async Task AcquisitionPolicy_PreservesSkipAndFallback(string mode, bool skip, bool executes)
    {
        var provider = new Mock<IDistributedLockProvider>();
        if (mode.StartsWith("backend"))
        {
            provider.Setup(value => value.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(mode == "backend-timeout" ? new TimeoutException("backend") : new InvalidOperationException("backend"));
        }
        IDistributedLockProvider service = mode == "absent" ? null : provider.Object;
        var calls = 0;
        var result = await service.RunWithOptionalLockAsync("work", TimeSpan.FromMilliseconds(10),
            (acquired, _) => { Assert.False(acquired); calls++; return Task.FromResult(42); }, Mock.Of<ILogger>(), skipOnTimeout: skip);
        Assert.Equal(executes, result.Executed);
        Assert.Equal(executes ? 1 : 0, calls);
    }

    [Fact]
    public async Task OperationFailure_IsNeverRetriedWithoutCoordination()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TestDistributedLockProvider().RunWithOptionalLockAsync<int>(
            "work", TimeSpan.Zero, (_, _) => { calls++; throw new InvalidOperationException("operation"); }, Mock.Of<ILogger>()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DetectedLoss_CancelsWorkAndSuppressesSuccess()
    {
        using var lost = new CancellationTokenSource();
        var ownership = new Mock<IDistributedLockOwnership>();
        ownership.SetupGet(value => value.HandleLostToken).Returns(lost.Token);
        var provider = new Mock<IDistributedLockProvider>();
        provider.Setup(value => value.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(ownership.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Object.RunWithOptionalLockAsync("work", TimeSpan.Zero,
            (_, token) => { lost.Cancel(); Assert.True(token.IsCancellationRequested); return Task.FromResult(42); }, Mock.Of<ILogger>()));
        ownership.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Cancellation_DoesNotAbandonUncooperativeWorkOrReleaseHealthyOwnership()
    {
        var provider = new TestDistributedLockProvider();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = provider.RunWithOptionalLockAsync("work", TimeSpan.Zero,
            async (_, _) => { started.TrySetResult(); await complete.Task; return 42; }, Mock.Of<ILogger>(), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            Assert.Null(await provider.TryAcquireAsync("work"));
        }
        finally { complete.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        await using var successor = await provider.TryAcquireAsync("work");
        Assert.NotNull(successor);
    }

    [Fact]
    public async Task CanceledRequest_WithMissingService_DoesNotFallBack()
    {
        IDistributedLockProvider provider = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RunWithOptionalLockAsync<int>("work", TimeSpan.Zero,
            (_, _) => throw new InvalidOperationException("Must not execute"), Mock.Of<ILogger>(), new CancellationToken(true)));
    }
}
