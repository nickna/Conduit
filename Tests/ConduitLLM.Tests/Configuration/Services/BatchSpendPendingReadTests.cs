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

/// <summary>Trusted group reads share financial totals without caching key membership.</summary>
public sealed class BatchSpendPendingReadTests
{
    [Fact]
    public async Task GetPendingSpendForGroupAsync_FreshGroup_CombinesAllFourTotalsWithoutReadingKeyAsync()
    {
        using var state = new State();
        state.Database.Setup(value => value.StringGetAsync(
                It.Is<RedisKey[]>(keys => keys.SequenceEqual(new RedisKey[]
                {
                    "pending_spend:group:91", "pending_spend_units:group:91",
                    "reserved_spend:group:91", "pending_spend_window_total_units:group:91"
                })), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue[] { "3.25", "125000000", "1.75", "25000000" });

        IBatchSpendUpdateService service = state.Batch;
        Assert.Equal(6.5m, await service.GetPendingSpendForGroupAsync(91));

        state.Store.VerifyNoOtherCalls();
        state.Database.Verify(value => value.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetPendingSpendAsync_ChangedOrMissingKey_ResolvesMembershipOnEveryCallAsync()
    {
        using var state = new State();
        state.Store.SetupSequence(value => value.GetByIdAsync(29, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Key(7)).ReturnsAsync(Key(91)).ReturnsAsync((VirtualKeyRuntimeRecord?)null);
        var groups = new List<string>();
        state.Database.Setup(value => value.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey[], CommandFlags>((keys, _) => groups.Add(keys[0].ToString()))
            .ReturnsAsync(new RedisValue[] { "0.5", "25000000", "0.125", "12500000" });

        Assert.Equal(1m, await state.Batch.GetPendingSpendAsync(29));
        Assert.Equal(1m, await state.Batch.GetPendingSpendAsync(29));
        Assert.Equal(0m, await state.Batch.GetPendingSpendAsync(29));

        Assert.Equal(new[] { "pending_spend:group:7", "pending_spend:group:91" }, groups);
        state.Store.Verify(value => value.GetByIdAsync(29, It.IsAny<CancellationToken>()), Times.Exactly(3));
        state.Database.Verify(value => value.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TryReserveSpendAsync_AfterGroupPendingRead_StillReadsCurrentGroupAndBalanceAsync()
    {
        using var state = new State();
        state.Database.Setup(value => value.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue[] { "0", "0", "0", "0" });
        state.Store.Setup(value => value.GetByIdAsync(29, It.IsAny<CancellationToken>())).ReturnsAsync(Key(91, balance: 1m));
        state.Database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(),
                It.Is<RedisKey[]>(keys => keys[0] == "pending_spend:group:91" && keys[1] == "reserved_spend:group:91"),
                It.Is<RedisValue[]>(values => values[0] == "1" && values[1] == "2"), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)0));

        Assert.Equal(0m, await state.Batch.GetPendingSpendForGroupAsync(7));
        Assert.False(await state.Batch.TryReserveSpendAsync(29, 2m, "fresh-admission"));

        state.Store.Verify(value => value.GetByIdAsync(29, It.IsAny<CancellationToken>()), Times.Once);
        state.Database.Verify(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetPendingSpendAsync_RedisFailure_PreservesFallbackForBothReadPathsAsync()
    {
        using var state = new State();
        state.Store.Setup(value => value.GetByIdAsync(29, It.IsAny<CancellationToken>())).ReturnsAsync(Key(7));
        state.Database.Setup(value => value.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisException("pending spend unavailable"));

        Assert.Equal(0m, await state.Batch.GetPendingSpendForGroupAsync(7));
        Assert.Equal(0m, await state.Batch.GetPendingSpendAsync(29));

        state.Store.Verify(value => value.GetByIdAsync(29, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static VirtualKeyRuntimeRecord Key(int groupId, decimal balance = 10m) => new()
    {
        Id = 29, VirtualKeyGroupId = groupId,
        Group = new VirtualKeyGroupRuntimeRecord { Id = groupId, Balance = balance }
    };

    private sealed class State : IDisposable
    {
        public Mock<IVirtualKeyRuntimeStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IDatabase> Database { get; } = new(MockBehavior.Strict);
        public BatchSpendUpdateService Batch { get; }

        public State()
        {
            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Database.Object);
            Batch = new BatchSpendUpdateService(Mock.Of<IServiceScopeFactory>(),
                new TestRedisConnectionFactory(redis.Object), Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions()),
                NullLogger<BatchSpendUpdateService>.Instance, Mock.Of<IBillingAlertingService>(), runtimeStore: Store.Object);
        }

        public void Dispose() => Batch.Dispose();
    }
}
