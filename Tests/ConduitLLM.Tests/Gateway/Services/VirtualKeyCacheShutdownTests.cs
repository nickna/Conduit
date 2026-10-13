using ConduitLLM.Gateway.Services;
using ConduitLLM.Configuration.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class VirtualKeyCacheShutdownTests
{
    [Fact]
    public async Task DisposeBeforeShutdown_StillJoinsIssuedCallbackWithoutUnsubscribeIo()
    {
        var database = new Mock<IDatabase>();
        var subscriber = new Mock<ISubscriber>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        redis.Setup(connection => connection.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        Action<RedisChannel, RedisValue>? handler = null;
        subscriber.Setup(service => service.Subscribe(RedisChannel.Literal(CacheKeys.VirtualKey.InvalidationChannel),
                It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, callback, _) => handler = callback);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(release.Task);
        using var cache = new RedisVirtualKeyCache(redis.Object, NullLogger<RedisVirtualKeyCache>.Instance);
        handler!(RedisChannel.Literal(CacheKeys.VirtualKey.InvalidationChannel), "held");
        cache.Dispose();
        var shutdown = cache.ShutdownAsync(CancellationToken.None);
        Assert.False(shutdown.IsCompleted);
        release.TrySetResult(true);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        subscriber.Verify(service => service.UnsubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task DisposeDuringFirstUnsubscribe_JoinsItWithoutIssuingSecondUnsubscribe()
    {
        var database = new Mock<IDatabase>();
        var subscriber = new Mock<ISubscriber>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        redis.Setup(connection => connection.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.Setup(service => service.UnsubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>())).Returns(release.Task);
        using var cache = new RedisVirtualKeyCache(redis.Object, NullLogger<RedisVirtualKeyCache>.Instance);
        var shutdown = cache.ShutdownAsync(CancellationToken.None);
        subscriber.Verify(service => service.UnsubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Once);
        cache.Dispose();
        Assert.False(shutdown.IsCompleted);
        release.TrySetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        subscriber.Verify(service => service.UnsubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SubscriptionShutdown_JoinsIssuedDeleteAndClosesLaterCallbackAdmission(bool canceledShutdown, bool batchMessage)
    {
        var database = new Mock<IDatabase>();
        var subscriber = new Mock<ISubscriber>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        redis.Setup(connection => connection.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        Action<RedisChannel, RedisValue>? acceptedHandler = null;
        var channel = RedisChannel.Literal(batchMessage ? CacheKeys.VirtualKey.BatchInvalidationChannel : CacheKeys.VirtualKey.InvalidationChannel);
        subscriber.Setup(service => service.Subscribe(channel,
                It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => acceptedHandler = handler);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Callback(() => entered.TrySetResult()).Returns(release.Task);
        using var cache = new RedisVirtualKeyCache(redis.Object, NullLogger<RedisVirtualKeyCache>.Instance);
        acceptedHandler!(channel, batchMessage ? "{\"KeyHashes\":[\"first\",\"second\"]}" : "first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        if (canceledShutdown) canceled.Cancel();
        var shutdown = cache.ShutdownAsync(canceled.Token);
        Assert.False(shutdown.IsCompleted);
        acceptedHandler(channel, "ignored-after-close");
        database.Verify(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Exactly(batchMessage ? 2 : 1));
        release.TrySetResult(true);
        if (canceledShutdown) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown);
        else await shutdown;
        subscriber.Verify(service => service.UnsubscribeAsync(It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()),
            canceledShutdown ? Times.Never() : Times.Exactly(2));
        var operations = subscriber.Invocations.Count + database.Invocations.Count;
        cache.Dispose();
        cache.Dispose();
        Assert.Equal(operations, subscriber.Invocations.Count + database.Invocations.Count);
    }

    [Fact]
    public async Task OptionalLifecycleWithoutRedis_DoesNotResolveOrOperateACache()
    {
        var lifecycle = new RedisVirtualKeyCacheLifecycleHostedService(null);
        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);
        await lifecycle.StoppedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BulkInvalidation_CancellationJoinsDeletesAndPreventsPublishOrStatistics()
    {
        var database = new Mock<IDatabase>();
        var subscriber = new Mock<ISubscriber>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        redis.Setup(connection => connection.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        var deletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        database.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Callback(() => { if (Interlocked.Increment(ref count) == 2) deletes.TrySetResult(); }).Returns(release.Task);
        using var cache = new RedisVirtualKeyCache(redis.Object, NullLogger<RedisVirtualKeyCache>.Instance);
        using var canceled = new CancellationTokenSource();
        var invalidation = cache.InvalidateVirtualKeysAsync(["first", "second"], canceled.Token);
        await deletes.Task.WaitAsync(TimeSpan.FromSeconds(10));
        canceled.Cancel();
        Assert.False(invalidation.IsCompleted);
        release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invalidation);
        subscriber.Verify(service => service.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()), Times.Never);
        database.Verify(db => db.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }
}
