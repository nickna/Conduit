using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.Logging;

using Moq;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Services;

public sealed class RedisWebhookConnectionTrackerTests
{
    private const string Connection = "owned-test-connection";
    private static readonly RedisKey ConnectionKey = RedisKeys.WebhookConnection.ConnectionWebhooks(Connection);
    private static readonly RedisKey TimestampKey = RedisKeys.WebhookConnection.ConnectionTimestamp(Connection);

    [Fact]
    public async Task EmptySubscriptionsStillDeleteBothConnectionKeysAndRepeatedCleanupIsHarmless()
    {
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None)).ReturnsAsync([]);
        var transaction = new Mock<ITransaction>(MockBehavior.Strict);
        transaction.Setup(value => value.KeyDeleteAsync(ConnectionKey, CommandFlags.None)).ReturnsAsync(false);
        transaction.Setup(value => value.KeyDeleteAsync(TimestampKey, CommandFlags.None)).ReturnsAsync(false);
        transaction.Setup(value => value.ExecuteAsync(CommandFlags.None)).ReturnsAsync(true);
        database.Setup(value => value.CreateTransaction(It.IsAny<object?>())).Returns(transaction.Object);
        var logger = new CaptureLogger();
        var tracker = Create(database, logger);

        await tracker.RemoveConnectionAsync(Connection);
        await tracker.RemoveConnectionAsync(Connection);

        database.Verify(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None), Times.Exactly(2));
        database.Verify(value => value.CreateTransaction(It.IsAny<object?>()), Times.Exactly(2));
        transaction.Verify(value => value.KeyDeleteAsync(ConnectionKey, CommandFlags.None), Times.Exactly(2));
        transaction.Verify(value => value.KeyDeleteAsync(TimestampKey, CommandFlags.None), Times.Exactly(2));
        transaction.Verify(value => value.ExecuteAsync(CommandFlags.None), Times.Exactly(2));
        transaction.VerifyNoOtherCalls();
        database.VerifyNoOtherCalls();
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal($"Removed connection {Connection} and its 0 webhook subscriptions", entry.Message);
        });
    }

    [Fact]
    public async Task ActiveSubscriptionsRemoveOnlyThisMemberAndDeleteOnlyConnectionOwnedKeys()
    {
        const string url = "https://receiver.example/webhook";
        RedisKey reverseKey = RedisKeys.WebhookConnection.WebhookConnections(Sha256Hash.LegacyStorageBase64Url(url)[..16]);
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None))
            .ReturnsAsync([(RedisValue)url]);
        var transaction = new Mock<ITransaction>(MockBehavior.Strict);
        transaction.Setup(value => value.SetRemoveAsync(reverseKey, (RedisValue)Connection, CommandFlags.None))
            .ReturnsAsync(true);
        transaction.Setup(value => value.KeyDeleteAsync(ConnectionKey, CommandFlags.None))
            .ReturnsAsync(true);
        transaction.Setup(value => value.KeyDeleteAsync(TimestampKey, CommandFlags.None))
            .ReturnsAsync(true);
        transaction.Setup(value => value.ExecuteAsync(CommandFlags.None))
            .ReturnsAsync(true);
        database.Setup(value => value.CreateTransaction(It.IsAny<object?>())).Returns(transaction.Object);
        var logger = new CaptureLogger();
        var tracker = Create(database, logger);

        await tracker.RemoveConnectionAsync(Connection);

        database.Verify(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None), Times.Once);
        database.Verify(value => value.CreateTransaction(It.IsAny<object?>()), Times.Once);
        transaction.Verify(value => value.SetRemoveAsync(reverseKey, (RedisValue)Connection, CommandFlags.None), Times.Once);
        transaction.Verify(value => value.KeyDeleteAsync(ConnectionKey, CommandFlags.None), Times.Once);
        transaction.Verify(value => value.KeyDeleteAsync(TimestampKey, CommandFlags.None), Times.Once);
        transaction.Verify(value => value.ExecuteAsync(CommandFlags.None), Times.Once);
        transaction.VerifyNoOtherCalls();
        database.VerifyNoOtherCalls();
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("execute")]
    [InlineData("cancelled-execute")]
    public async Task CleanupErrorsRetainTheOriginalExceptionAndDoNotLogSuccess(string stage)
    {
        Exception failure = stage == "cancelled-execute"
            ? new OperationCanceledException("Owned fixture cancellation") : new IOException("Owned fixture failure");
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        var transaction = new Mock<ITransaction>(MockBehavior.Strict);
        if (stage == "read")
            database.Setup(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None)).ThrowsAsync(failure);
        else
        {
            database.Setup(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None)).ReturnsAsync([]);
            database.Setup(value => value.CreateTransaction(It.IsAny<object?>())).Returns(transaction.Object);
            transaction.Setup(value => value.KeyDeleteAsync(ConnectionKey, CommandFlags.None)).ReturnsAsync(false);
            transaction.Setup(value => value.KeyDeleteAsync(TimestampKey, CommandFlags.None)).ReturnsAsync(false);
            transaction.Setup(value => value.ExecuteAsync(CommandFlags.None)).ThrowsAsync(failure);
        }
        var logger = new CaptureLogger();
        var tracker = Create(database, logger);

        // The existing service contract logs and swallows Redis failures.
        await tracker.RemoveConnectionAsync(Connection);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Equal($"Error removing connection {Connection}", entry.Message);
        database.Verify(value => value.SetMembersAsync(ConnectionKey, CommandFlags.None), Times.Once);
        database.Verify(value => value.CreateTransaction(It.IsAny<object?>()), stage == "read" ? Times.Never() : Times.Once());
        transaction.Verify(value => value.KeyDeleteAsync(ConnectionKey, CommandFlags.None), stage == "read" ? Times.Never() : Times.Once());
        transaction.Verify(value => value.KeyDeleteAsync(TimestampKey, CommandFlags.None), stage == "read" ? Times.Never() : Times.Once());
        transaction.Verify(value => value.ExecuteAsync(CommandFlags.None), stage == "read" ? Times.Never() : Times.Once());
        transaction.VerifyNoOtherCalls();
        database.VerifyNoOtherCalls();
    }

    private static RedisWebhookConnectionTracker Create(Mock<IDatabase> database, CaptureLogger logger)
    {
        var redis = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database.Object);
        return new RedisWebhookConnectionTracker(redis.Object, logger);
    }

    private sealed class CaptureLogger : ILogger<RedisWebhookConnectionTracker>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
