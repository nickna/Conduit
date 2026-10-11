using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services;

[Trait("Category", "Unit")]
[Trait("Component", "BatchSpendUpdateService")]
public class BatchSpendUpdateServiceFallbackTests
{
    private const int KeyId = 41;
    private const int GroupId = 13;
    private const decimal Cost = 2.75m;
    private const string KeyHash = "fallback-hash-41";
    private static readonly DateTime BilledAtUtc = new(2026, 10, 11, 4, 37, 0, DateTimeKind.Utc);
    private static readonly DateTime BillingWindowUtc = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new(MockBehavior.Strict);
    private readonly Mock<IVirtualKeyRuntimeStore> _runtimeStore = new(MockBehavior.Strict);
    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<IServer> _server = new();
    private readonly Mock<ILogger<BatchSpendUpdateService>> _logger = new();
    private readonly TestRedisConnectionFactory _redisFactory;
    private readonly List<string[]> _notifications = [];

    public BatchSpendUpdateServiceFallbackTests()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var endpoint = new System.Net.DnsEndPoint("localhost", 6379);
        connection.Setup(redis => redis.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_database.Object);
        connection.Setup(redis => redis.GetServer(
                It.IsAny<System.Net.EndPoint>(), It.IsAny<object>()))
            .Returns(_server.Object);
        connection.Setup(redis => redis.GetEndPoints(It.IsAny<bool>())).Returns([endpoint]);
        _server.Setup(server => server.Keys(
                It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Returns(Array.Empty<RedisKey>());
        _redisFactory = new TestRedisConnectionFactory(connection.Object);
        _runtimeStore.Setup(store => store.GetByIdAsync(KeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyRuntimeRecord
            {
                Id = KeyId,
                KeyHash = KeyHash,
                VirtualKeyGroupId = GroupId,
                Group = new VirtualKeyGroupRuntimeRecord { Id = GroupId }
            });
        _runtimeStore.Setup(store => store.AdjustBalanceAsync(
                It.Is<VirtualKeyBalanceAdjustment>(adjustment =>
                    adjustment.GroupId == GroupId &&
                    adjustment.Amount == -Cost &&
                    adjustment.ReferenceId == "41" &&
                    adjustment.BillingWindowStartUtc == BillingWindowUtc),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(97.25m, Cost, true));
    }

    [Fact]
    public async Task FlushPendingUpdatesAsync_WithOnlyFallback_PersistsAndNotifiesOnce()
    {
        using var service = CreateService();
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        Assert.Equal(1, await service.FlushPendingUpdatesAsync());
        Assert.Equal(0, await service.FlushPendingUpdatesAsync());

        Assert.Equal([KeyHash], Assert.Single(_notifications));
        _runtimeStore.Verify(store => store.AdjustBalanceAsync(
            It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()), Times.Once);
        _scopeFactory.VerifyNoOtherCalls();
        var statistics = await service.GetStatisticsAsync();
        Assert.Equal(0, statistics["FallbackPendingUpdates"]);
        Assert.Equal(0m, statistics["FallbackPendingCost"]);
    }

    [Fact]
    public async Task FlushPendingUpdatesAsync_WithRedisClaimAndFallback_CountsBothAndNotifies()
    {
        const int redisGroupId = 22;
        const string claimId = "redis-claim";
        RedisKey claimKey = $"processing_spend:group:{redisGroupId}:claim:{claimId}";
        _server.Setup(server => server.Keys(
                It.IsAny<int>(), (RedisValue)"processing_spend:group:*", It.IsAny<int>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Returns([claimKey]);
        _database.Setup(db => db.StringGetAsync(claimKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync("1.25");
        _database.Setup(db => db.KeyDeleteAsync(
                It.Is<RedisKey[]>(keys => keys.SequenceEqual(new[] { claimKey })),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);
        _runtimeStore.Setup(store => store.AdjustBalanceAsync(
                It.Is<VirtualKeyBalanceAdjustment>(adjustment =>
                    adjustment.GroupId == redisGroupId && adjustment.Amount == -1.25m &&
                    adjustment.IdempotencyKey == $"batch-spend:{claimId}"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(98.75m, 1.25m, true));
        _runtimeStore.Setup(store => store.GetKeyHashesByGroupIdAsync(
                redisGroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["redis-key-hash"]);
        using var service = CreateService();
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        Assert.Equal(2, await service.FlushPendingUpdatesAsync());

        Assert.Equal(["redis-key-hash", KeyHash], Assert.Single(_notifications));
        _runtimeStore.VerifyAll();
        _database.Verify(db => db.KeyDeleteAsync(
            It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task FlushPendingUpdatesAsync_WhenFallbackWriteFails_RetainsBacklogAndRetries()
    {
        _runtimeStore.SetupSequence(store => store.AdjustBalanceAsync(
                It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"))
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(97.25m, Cost, true));
        using var service = CreateService();
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        Assert.Equal(0, await service.FlushPendingUpdatesAsync());
        Assert.Empty(_notifications);
        var failedStatistics = await service.GetStatisticsAsync();
        Assert.Equal(1, failedStatistics["FallbackPendingUpdates"]);
        Assert.Equal(Cost, failedStatistics["FallbackPendingCost"]);

        Assert.Equal(1, await service.FlushPendingUpdatesAsync());
        Assert.Equal([KeyHash], Assert.Single(_notifications));
        var recoveredStatistics = await service.GetStatisticsAsync();
        Assert.Equal(0, recoveredStatistics["FallbackPendingUpdates"]);
        Assert.Equal(0m, recoveredStatistics["FallbackPendingCost"]);
        _runtimeStore.Verify(store => store.AdjustBalanceAsync(
            It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task FlushPendingUpdatesAsync_WithPartialFallbackFailure_NotifiesSuccessfulWrites()
    {
        _runtimeStore.SetupSequence(store => store.AdjustBalanceAsync(
                It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(97.25m, Cost, true))
            .ThrowsAsync(new InvalidOperationException("database unavailable"))
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(94.50m, Cost * 2, true));
        using var service = CreateService();
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        Assert.Equal(1, await service.FlushPendingUpdatesAsync());
        Assert.Equal([KeyHash], Assert.Single(_notifications));
        Assert.Equal(1, (await service.GetStatisticsAsync())["FallbackPendingUpdates"]);
        Assert.Equal(1, await service.FlushPendingUpdatesAsync());
        Assert.Equal(2, _notifications.Count);
    }

    [Fact]
    public async Task StopAsync_WithOnlyFallback_DrainsDuringFinalFlushAndMarksStopped()
    {
        using var service = CreateService();
        await StartAfterEmptyStartupFlushAsync(service);
        Assert.True(service.IsHealthy);
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal([KeyHash], Assert.Single(_notifications));
        Assert.Equal(0, (await service.GetStatisticsAsync())["FallbackPendingUpdates"]);
        Assert.False(service.IsHealthy);
        _runtimeStore.Verify(store => store.AdjustBalanceAsync(
            It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WithOnlyFallback_DrainsFinalUpdates()
    {
        var service = CreateService();
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        await service.DisposeAsync();

        Assert.Equal([KeyHash], Assert.Single(_notifications));
        _runtimeStore.Verify(store => store.AdjustBalanceAsync(
            It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PeriodicFlush_WithOnlyFallback_DrainsAndKeepsServiceHealthy()
    {
        using var service = CreateService(flushIntervalSeconds: 1);
        await StartAfterEmptyStartupFlushAsync(service);
        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SpendUpdatesCompleted += _ => recovered.TrySetResult(true);
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(service.IsHealthy);
        Assert.Equal(0, (await service.GetStatisticsAsync())["FallbackPendingUpdates"]);
        await service.StopAsync(CancellationToken.None);
        _runtimeStore.Verify(store => store.AdjustBalanceAsync(
            It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStatisticsAsync_WhenRedisFails_StillReportsFallbackBacklog()
    {
        _server.Setup(server => server.Keys(
                It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline"));
        using var service = CreateService();
        service.QueueFallbackUpdate(KeyId, Cost, BilledAtUtc);

        var statistics = await service.GetStatisticsAsync();

        Assert.Contains("Error", statistics.Keys);
        Assert.Equal(1, statistics["FallbackPendingUpdates"]);
        Assert.Equal(Cost, statistics["FallbackPendingCost"]);
    }

    private BatchSpendUpdateService CreateService(int flushIntervalSeconds = 3600)
    {
        var service = new BatchSpendUpdateService(
            _scopeFactory.Object,
            _redisFactory,
            Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = flushIntervalSeconds }),
            _logger.Object,
            Mock.Of<IBillingAlertingService>(),
            runtimeStore: _runtimeStore.Object);
        service.SpendUpdatesCompleted += hashes => _notifications.Add(hashes);
        return service;
    }

    private async Task StartAfterEmptyStartupFlushAsync(BatchSpendUpdateService service)
    {
        var startupFlushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logger.Setup(logger => logger.Log(
                LogLevel.Debug, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString() == "No pending spend updates to flush"),
                It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()))
            .Callback(() => startupFlushed.TrySetResult(true));
        await service.StartAsync(CancellationToken.None);
        await startupFlushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
