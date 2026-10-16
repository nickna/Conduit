using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Helpers;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Gateway.Middleware;

public sealed class SpendDebitRecoveryTests
{
    private const int GroupId = 13;
    private const int KeyId = 41;
    private const decimal Cost = 2.75m;
    private static readonly DateTime BilledAtUtc = new(2026, 10, 11, 4, 37, 0, DateTimeKind.Utc);
    private static readonly DateTime BillingWindowUtc = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("cached", false)]
    [InlineData("cached", true)]
    [InlineData("store", false)]
    [InlineData("store", true)]
    public async Task UpdateSpendAsync_WhenDebitCommitsBeforeTimeout_FallbackRecoveryDoesNotDebitAgain(
        string runtime, bool storeBackedRecovery)
    {
        using var database = new SqliteTestDatabase();
        var group = new VirtualKeyGroup
        {
            Id = GroupId, GroupName = "Debit recovery", Balance = 100m, LifetimeCreditsAdded = 100m
        };
        var key = new VirtualKey
        {
            Id = KeyId, KeyName = "Recovery key", KeyHash = "recovery-key-hash",
            VirtualKeyGroupId = GroupId, VirtualKeyGroup = group
        };
        database.Seed(context =>
        {
            context.VirtualKeyGroups.Add(group);
            context.VirtualKeys.Add(key);
            context.SaveChanges();
        });
        var repository = new VirtualKeyGroupRepository(database.CreateDbContextFactory(),
            NullLogger<VirtualKeyGroupRepository>.Instance);
        var store = new EfVirtualKeyRuntimeStore(database.CreateDbContextFactory());
        string attemptedIdentity = null;

        var ambiguousRepository = new Mock<IVirtualKeyGroupRepository>(MockBehavior.Strict);
        ambiguousRepository.Setup(value => value.GetByIdAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        ambiguousRepository.Setup(value => value.GetByKeyIdAsync(KeyId)).ReturnsAsync(group);
        ambiguousRepository.Setup(value => value.AdjustBalanceIdempotentAsync(
                GroupId, -Cost, It.IsAny<string>(), It.IsAny<string>(), "System",
                ReferenceType.VirtualKey, KeyId.ToString(), BillingWindowUtc))
            .Returns(async (int groupId, decimal amount, string identity, string description,
                string actor, ReferenceType referenceType, string referenceId, DateTime window) =>
            {
                attemptedIdentity = identity;
                await repository.AdjustBalanceIdempotentAsync(groupId, amount, identity,
                    description, actor, referenceType, referenceId, window);
                throw new TimeoutException("Commit succeeded, but its acknowledgement was lost");
            });
        var ambiguousStore = new Mock<IVirtualKeyRuntimeStore>(MockBehavior.Strict);
        ambiguousStore.Setup(value => value.GetByIdAsync(KeyId, It.IsAny<CancellationToken>()))
            .Returns((int id, CancellationToken token) => store.GetByIdAsync(id, token));
        ambiguousStore.Setup(value => value.AdjustBalanceAsync(
                It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
            .Returns(async (VirtualKeyBalanceAdjustment adjustment, CancellationToken token) =>
            {
                attemptedIdentity = adjustment.IdempotencyKey;
                await store.AdjustBalanceAsync(adjustment, token);
                throw new TimeoutException("Commit succeeded, but its acknowledgement was lost");
            });
        var keys = new Mock<IVirtualKeyRepository>(MockBehavior.Strict);
        keys.Setup(value => value.GetByIdAsync(KeyId, It.IsAny<CancellationToken>())).ReturnsAsync(key);
        IVirtualKeyRuntimeService service = runtime switch
        {
            "direct" => new DirectApiVirtualKeyService(keys.Object, ambiguousRepository.Object,
                Mock.Of<IVirtualKeySpendHistoryRepository>(), null,
                NullLogger<DirectApiVirtualKeyService>.Instance),
            "cached" => new CachedApiVirtualKeyService(keys.Object,
                Mock.Of<IVirtualKeySpendHistoryRepository>(), ambiguousRepository.Object,
                Mock.Of<IVirtualKeyCache>(), null, NullLogger<CachedApiVirtualKeyService>.Instance),
            _ => new StoreBackedVirtualKeyRuntimeService(ambiguousStore.Object,
                NullLogger<StoreBackedVirtualKeyRuntimeService>.Instance)
        };
        var services = new ServiceCollection();
        services.AddScoped<IConfigurationDbContext>(_ => database.CreateContext());
        services.AddSingleton<IVirtualKeyGroupRepository>(repository);
        using var provider = services.BuildServiceProvider();
        using var redis = CreateRedisFactory();
        using var batch = new BatchSpendUpdateService(provider.GetRequiredService<IServiceScopeFactory>(),
            redis, Options.Create(new BatchSpendingOptions()), NullLogger<BatchSpendUpdateService>.Instance,
            Mock.Of<IBillingAlertingService>(), runtimeStore: storeBackedRecovery ? store : null);

        await Assert.ThrowsAsync<BillingSystemException>(() => SpendUpdateHelper.UpdateSpendAsync(
            KeyId, Cost, batch, service, NullLogger.Instance, BilledAtUtc));

        Assert.StartsWith("spend:", attemptedIdentity);
        await AssertSingleDebitAsync(database, attemptedIdentity);
        Assert.Equal(1, (await batch.GetStatisticsAsync())["FallbackPendingUpdates"]);

        Assert.Equal(1, await batch.FlushPendingUpdatesAsync());
        Assert.Equal(0, await batch.FlushPendingUpdatesAsync());

        Assert.Equal(0, (await batch.GetStatisticsAsync())["FallbackPendingUpdates"]);
        await AssertSingleDebitAsync(database, attemptedIdentity);
    }

    private static async Task AssertSingleDebitAsync(SqliteTestDatabase database, string identity)
    {
        await using var context = database.CreateContext();
        var group = await context.VirtualKeyGroups.SingleAsync(value => value.Id == GroupId);
        Assert.Equal(100m - Cost, group.Balance);
        Assert.Equal(Cost, group.LifetimeSpent);
        var debit = Assert.Single(await context.VirtualKeyGroupTransactions.ToListAsync());
        Assert.Equal(Cost, debit.Amount);
        Assert.Equal(identity, debit.IdempotencyKey);
        Assert.Equal(ReferenceType.VirtualKey, debit.ReferenceType);
        Assert.Equal(KeyId.ToString(), debit.ReferenceId);
        Assert.Equal(BillingWindowUtc, debit.BillingWindowStartUtc);
    }

    private static TestRedisConnectionFactory CreateRedisFactory()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var redis = new Mock<IDatabase>();
        var server = new Mock<IServer>();
        connection.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(redis.Object);
        connection.Setup(value => value.GetEndPoints(It.IsAny<bool>()))
            .Returns([new System.Net.DnsEndPoint("localhost", 6379)]);
        connection.Setup(value => value.GetServer(It.IsAny<System.Net.EndPoint>(), It.IsAny<object>()))
            .Returns(server.Object);
        server.Setup(value => value.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Returns(Array.Empty<RedisKey>());
        return new TestRedisConnectionFactory(connection.Object);
    }
}
