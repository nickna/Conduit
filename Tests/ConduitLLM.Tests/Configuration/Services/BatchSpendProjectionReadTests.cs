using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class BatchSpendProjectionReadTests
{
    [Fact]
    public async Task GetPendingSpendAsync_ProjectionChangesOrDisappears_UsesFreshMembershipAsync()
    {
        using var state = new State();
        state.Snapshots.SetupSequence(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeySpendSnapshot(7, 10m))
            .ReturnsAsync(new VirtualKeySpendSnapshot(91, 2m))
            .Returns(Task.FromResult<VirtualKeySpendSnapshot?>(null));
        var groups = new List<string>();
        state.Database.Setup(value => value.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey[], CommandFlags>((keys, _) => groups.Add(keys[0].ToString()))
            .ReturnsAsync(new RedisValue[] { "1", "0", "0", "0" });

        Assert.Equal(1m, await state.Batch.GetPendingSpendAsync(29));
        Assert.Equal(1m, await state.Batch.GetPendingSpendAsync(29));
        Assert.Equal(0m, await state.Batch.GetPendingSpendAsync(29));

        Assert.Equal(new[] { "pending_spend:group:7", "pending_spend:group:91" }, groups);
        state.Snapshots.Verify(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()), Times.Exactly(3));
        state.Store.Verify(value => value.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryReserveSpendAsync_ProjectionBalanceChangesOrDisappears_ReadsEachAdmissionAsync()
    {
        using var state = new State();
        state.Snapshots.SetupSequence(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeySpendSnapshot(7, 10m))
            .ReturnsAsync(new VirtualKeySpendSnapshot(7, 1m))
            .ReturnsAsync(new VirtualKeySpendSnapshot(91, 0m))
            .Returns(Task.FromResult<VirtualKeySpendSnapshot?>(null));
        var admissions = new List<(string Group, string Balance)>();
        state.Database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, keys, values, _) =>
                admissions.Add((keys[0].ToString(), values[0].ToString())))
            .ReturnsAsync((string _, RedisKey[] _, RedisValue[] values, CommandFlags _) =>
                RedisResult.Create((RedisValue)(decimal.Parse(values[0].ToString(), System.Globalization.CultureInfo.InvariantCulture) >= 2m ? 1 : 0)));

        Assert.True(await state.Batch.TryReserveSpendAsync(29, 2m, "first"));
        Assert.False(await state.Batch.TryReserveSpendAsync(29, 2m, "balance-changed"));
        Assert.False(await state.Batch.TryReserveSpendAsync(29, 2m, "group-changed"));
        Assert.False(await state.Batch.TryReserveSpendAsync(29, 2m, "missing"));

        Assert.Equal(new[]
        {
            ("pending_spend:group:7", "10"), ("pending_spend:group:7", "1"), ("pending_spend:group:91", "0")
        }, admissions);
        state.Snapshots.Verify(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()), Times.Exactly(4));
        state.Store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SpendStages_ProjectionMembershipChanges_KeepEveryFreshReadAsync()
    {
        using var state = new State();
        state.Snapshots.SetupSequence(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeySpendSnapshot(7, 10m))
            .ReturnsAsync(new VirtualKeySpendSnapshot(91, 10m))
            .ReturnsAsync(new VirtualKeySpendSnapshot(17, 10m))
            .ReturnsAsync(new VirtualKeySpendSnapshot(51, 10m));
        var groups = new List<string>();
        state.Database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, keys, _, _) => groups.Add(keys[0].ToString()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1));

        Assert.True(await state.Batch.TryReserveSpendAsync(29, 1m, "reservation"));
        Assert.True(await state.Batch.MarkSpendReservationInvocationStartedAsync(29, "reservation"));
        Assert.Equal(SpendReservationSettlementStatus.Settled,
            (await state.Batch.SettleSpendReservationAsync(29, "reservation", 1m)).Status);
        await state.Batch.ReleaseSpendReservationAsync(29, "reservation");

        Assert.Equal(new[]
        {
            "pending_spend:group:7", "spend_reservations:group:91",
            "spend_reservations:group:17", "spend_reservations:group:51"
        }, groups);
        state.Snapshots.Verify(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()), Times.Exactly(4));
        state.Store.Verify(value => value.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SpendStages_MissingProjection_PreserveMissingOutcomesWithoutRedisAsync()
    {
        using var state = new State();
        state.Snapshots.Setup(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<VirtualKeySpendSnapshot?>(null));

        Assert.False(await state.Batch.TryReserveSpendAsync(29, 1m, "missing"));
        Assert.False(await state.Batch.MarkSpendReservationInvocationStartedAsync(29, "missing"));
        Assert.Equal(SpendReservationSettlementStatus.Missing,
            (await state.Batch.SettleSpendReservationAsync(29, "missing", 1m)).Status);
        await state.Batch.ReleaseSpendReservationAsync(29, "missing");

        state.Snapshots.Verify(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()), Times.Exactly(4));
        state.Database.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TryReserveSpendAsync_ProjectionReadFails_PropagatesWithoutUsingStaleGraphAsync()
    {
        using var state = new State();
        state.Snapshots.Setup(value => value.GetSpendSnapshotAsync(29, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("fresh database read unavailable"));

        await Assert.ThrowsAsync<TimeoutException>(() => state.Batch.TryReserveSpendAsync(29, 1m, "unavailable"));

        state.Store.Verify(value => value.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Database.VerifyNoOtherCalls();
    }

    private sealed class State : IDisposable
    {
        public Mock<IVirtualKeyRuntimeStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IVirtualKeySpendSnapshotStore> Snapshots { get; }
        public Mock<IDatabase> Database { get; } = new(MockBehavior.Strict);
        public BatchSpendUpdateService Batch { get; }

        public State()
        {
            Snapshots = Store.As<IVirtualKeySpendSnapshotStore>();
            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Database.Object);
            Batch = new BatchSpendUpdateService(Mock.Of<IServiceScopeFactory>(),
                new TestRedisConnectionFactory(redis.Object), Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions()),
                NullLogger<BatchSpendUpdateService>.Instance, Mock.Of<IBillingAlertingService>(), runtimeStore: Store.Object);
        }

        public void Dispose() => Batch.Dispose();
    }
}
