using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class BatchSpendShutdownFaultTests(BillingFaultFixture fixture)
{
    private static readonly DateTime BilledAt = new(2026, 7, 25, 23, 59, 59, DateTimeKind.Utc);

    [Fact(Timeout = 90_000)]
    public async Task CanceledAfterNativeClaimCommit_BeforeRedisAcknowledgement_RecoveryDebitsOnce()
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var factory = CreateRedisFactory();
        using var canceled = new CancellationTokenSource();
        var native = new NpgsqlVirtualKeyRuntimeStore(dataSource);
        var store = Proxy(native);
        store.Setup(service => service.AdjustBalanceAsync(It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
            .Returns(async (VirtualKeyBalanceAdjustment adjustment, CancellationToken token) =>
            {
                var result = await native.AdjustBalanceAsync(adjustment, token);
                canceled.Cancel();
                return result;
            });
        await using var batch = CreateBatch(factory, store.Object);
        await batch.QueueSpendUpdateAsync(account.KeyId, 3m, BilledAt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.FlushPendingUpdatesAsync(canceled.Token));
        await AssertDebitAsync(account.GroupId);
        Assert.Equal(3m, await batch.GetPendingSpendAsync(account.KeyId));
        store.Verify(service => service.GetKeyHashesByGroupIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var recovered = fixture.CreateBatchService();
        Assert.Equal(1, await recovered.FlushPendingUpdatesAsync());
        Assert.Equal(0, await recovered.FlushPendingUpdatesAsync());
        Assert.Equal(0m, await recovered.GetPendingSpendAsync(account.KeyId));
        await AssertDebitAsync(account.GroupId);
    }

    [Theory(Timeout = 90_000)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task FallbackUnknownCommit_RetryUsesOriginalIdentityAndHour(bool cancellation, bool runtimeStore)
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var factory = CreateRedisFactory();
        using var canceled = new CancellationTokenSource();
        var native = new NpgsqlVirtualKeyRuntimeStore(dataSource);
        var store = Proxy(native);
        var attempts = new List<VirtualKeyBalanceAdjustment>();
        store.Setup(service => service.AdjustBalanceAsync(It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
            .Returns(async (VirtualKeyBalanceAdjustment adjustment, CancellationToken token) =>
            {
                attempts.Add(adjustment);
                var result = await native.AdjustBalanceAsync(adjustment, token);
                if (attempts.Count == 1)
                {
                    if (cancellation) canceled.Cancel();
                    else throw new IOException("connection lost after database commit");
                }
                return result;
            });
        var repository = new Mock<IVirtualKeyGroupRepository>();
        var realRepository = fixture.CreateGroupRepository();
        repository.Setup(service => service.AdjustBalanceIdempotentAsync(It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReferenceType>(),
                It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(async (int groupId, decimal amount, string identity, string description,
                string actor, ReferenceType referenceType, string referenceId, DateTime window) =>
            {
                attempts.Add(new VirtualKeyBalanceAdjustment(groupId, amount, description, actor,
                    VirtualKeyBalanceReferenceType.VirtualKey, referenceId, identity, window));
                var result = await realRepository.AdjustBalanceIdempotentAsync(groupId, amount, identity,
                    description, actor, referenceType, referenceId, window);
                if (attempts.Count == 1)
                {
                    if (cancellation) canceled.Cancel();
                    else throw new IOException("connection lost after database commit");
                }
                return result;
            });
        await using var batch = runtimeStore ? CreateBatch(factory, store.Object) : fixture.CreateBatchService(repository.Object);
        batch.QueueFallbackUpdate(account.KeyId, 3m, BilledAt);
        if (cancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.FlushPendingUpdatesAsync(canceled.Token));
        else Assert.Equal(0, await batch.FlushPendingUpdatesAsync());
        await AssertDebitAsync(account.GroupId);
        Assert.Equal(1, (await batch.GetStatisticsAsync())["FallbackPendingUpdates"]);
        Assert.Equal(1, await batch.FlushPendingUpdatesAsync());
        Assert.Equal(0, await batch.FlushPendingUpdatesAsync());
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].IdempotencyKey, attempts[1].IdempotencyKey);
        Assert.StartsWith("fallback-spend:", attempts[0].IdempotencyKey);
        Assert.All(attempts, item => Assert.Equal(BilledAt.Date.AddHours(23), item.BillingWindowStartUtc));
        Assert.Equal(0, (await batch.GetStatisticsAsync())["FallbackPendingUpdates"]);
        await AssertDebitAsync(account.GroupId);
    }

    private async Task AssertDebitAsync(int groupId)
    {
        await using var db = fixture.CreateDbContext();
        var group = await db.VirtualKeyGroups.SingleAsync(group => group.Id == groupId);
        Assert.Equal(97m, group.Balance);
        Assert.Equal(3m, group.LifetimeSpent);
        var debit = Assert.Single(await db.VirtualKeyGroupTransactions.Where(transaction =>
            transaction.VirtualKeyGroupId == groupId && transaction.TransactionType == TransactionType.Debit).ToListAsync());
        Assert.Equal(3m, debit.Amount);
        Assert.Equal(BilledAt.Date.AddHours(23), debit.BillingWindowStartUtc);
    }

    private RedisConnectionFactory CreateRedisFactory() => new(
        Options.Create(new CacheOptions { RedisConnectionString = fixture.RedisConnectionString }),
        NullLogger<RedisConnectionFactory>.Instance);

    private static BatchSpendUpdateService CreateBatch(RedisConnectionFactory factory, IVirtualKeyRuntimeStore store) => new(
        Mock.Of<IServiceScopeFactory>(), factory,
        Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600, MaximumIntervalSeconds = 7200 }),
        NullLogger<BatchSpendUpdateService>.Instance, Mock.Of<IBillingAlertingService>(), runtimeStore: store);

    private static Mock<IVirtualKeyRuntimeStore> Proxy(IVirtualKeyRuntimeStore native)
    {
        var store = new Mock<IVirtualKeyRuntimeStore>(MockBehavior.Strict);
        store.Setup(service => service.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int id, CancellationToken token) => native.GetByIdAsync(id, token));
        store.Setup(service => service.GetKeyHashesByGroupIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int id, CancellationToken token) => native.GetKeyHashesByGroupIdAsync(id, token));
        return store;
    }
}
