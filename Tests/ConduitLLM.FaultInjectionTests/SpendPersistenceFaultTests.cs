using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class SpendPersistenceFaultTests(BillingFaultFixture fixture)
{
    [Fact(Timeout = 90_000)]
    public async Task CommittedDebitBeforeRedisAcknowledgement_RecoveryDoesNotDoubleCharge()
    {
        const decimal cost = 0.0042m;
        var account = await fixture.SeedAccountAsync();
        var realRepository = fixture.CreateGroupRepository();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedRepository = new Mock<IVirtualKeyGroupRepository>();
        pausedRepository.Setup(repository => repository.AdjustBalanceIdempotentAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<ReferenceType>(), It.IsAny<string?>(), It.IsAny<DateTime>()))
            .Returns(async (int groupId, decimal amount, string idempotencyKey, string? description,
                string? initiatedBy, ReferenceType referenceType, string? referenceId, DateTime billingWindow) =>
            {
                var result = await realRepository.AdjustBalanceIdempotentAsync(
                    groupId, amount, idempotencyKey, description, initiatedBy, referenceType, referenceId, billingWindow);
                committed.TrySetResult();
                await acknowledge.Task;
                return result;
            });

        await using var batch = fixture.CreateBatchService(pausedRepository.Object);
        await batch.QueueSpendUpdateAsync(account.KeyId, cost, DateTime.UtcNow);
        var flush = batch.FlushPendingUpdatesAsync();
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var db = fixture.CreateDbContext();
            var debit = await db.VirtualKeyGroupTransactions.AsNoTracking().SingleAsync(
                transaction => transaction.VirtualKeyGroupId == account.GroupId);
            debit.Amount.Should().Be(cost);

            // Reproduce CI's transient double-accounting snapshot: the committed
            // debit and Redis claim represent the SAME charge until acknowledgement.
            await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
            var claim = redis.GetServer(redis.GetEndPoints()[0]).Keys(
                pattern: $"processing_spend_window_units:group:{account.GroupId}:window:*:claim:*").Single();
            var claimId = claim.ToString().Split(":claim:", StringSplitOptions.None)[1];
            debit.IdempotencyKey.Should().Be($"batch-spend:{claimId}");
            ((long)(await redis.GetDatabase().StringGetAsync(claim))).Should().Be(420_000);

            // Another process can recover the still-present claim without repeating
            // the debit, even while the original flusher has not acknowledged it.
            await using var recovery = fixture.CreateBatchService();
            (await recovery.FlushPendingUpdatesAsync()).Should().Be(1);
            await fixture.AssertAccountedForAsync(account.GroupId, cost);
            (await db.VirtualKeyGroupTransactions.CountAsync(
                transaction => transaction.VirtualKeyGroupId == account.GroupId)).Should().Be(1);
            (await db.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == account.GroupId))
                .Balance.Should().Be(100m - cost);
        }
        finally { acknowledge.TrySetResult(); await flush; }
    }

    [Fact(Timeout = 90_000)]
    public async Task RedisUnavailable_DuringQueue_PersistsSpendDirectlyToPostgres()
    {
        const decimal cost = 1.2345m;
        const decimal warmupCost = 0.0001m;
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();

        // Establish the multiplexer before cutting Redis so this fails at the queue write,
        // not while constructing the test harness.
        await batch.QueueSpendUpdateAsync(account.KeyId, warmupCost, DateTime.UtcNow);
        await batch.FlushPendingUpdatesAsync();
        await fixture.StopRedisAsync();

        try
        {
            var directService = new DirectApiVirtualKeyService(
                fixture.CreateKeyRepository(),
                fixture.CreateGroupRepository(),
                Mock.Of<IVirtualKeySpendHistoryRepository>(),
                null,
                NullLogger<DirectApiVirtualKeyService>.Instance);

            await SpendUpdateHelper.UpdateSpendAsync(
                account.KeyId,
                cost,
                batch,
                directService,
                NullLogger.Instance);
        }
        finally
        {
            await fixture.StartRedisAsync();
        }

        await fixture.AssertAccountedForAsync(account.GroupId, cost + warmupCost);
    }

    [Fact(Timeout = 120_000)]
    public async Task PostgresFailure_MidFlush_LeavesClaimsAndRetriesExactlyOnce()
    {
        const decimal firstCost = 2.25m;
        const decimal secondCost = 3.75m;
        var first = await fixture.SeedAccountAsync();
        var second = await fixture.SeedAccountAsync();
        var realRepository = fixture.CreateGroupRepository();
        var faultingRepository = new Mock<IVirtualKeyGroupRepository>();
        var calls = 0;

        faultingRepository.Setup(repository => repository.AdjustBalanceIdempotentAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<ReferenceType>(), It.IsAny<string?>(), It.IsAny<DateTime>()))
            .Returns(async (int groupId, decimal amount, string idempotencyKey, string? description,
                string? initiatedBy, ReferenceType referenceType, string? referenceId, DateTime billingWindow) =>
            {
                calls++;
                if (calls == 2)
                {
                    await fixture.StopPostgresAsync();
                }

                return await realRepository.AdjustBalanceIdempotentAsync(
                    groupId, amount, idempotencyKey, description, initiatedBy,
                    referenceType, referenceId, billingWindow);
            });

        await using var batch = fixture.CreateBatchService(faultingRepository.Object);
        var billedAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        await batch.QueueSpendUpdateAsync(first.KeyId, firstCost, billedAt);
        await batch.QueueSpendUpdateAsync(second.KeyId, secondCost, billedAt);

        await Assert.ThrowsAnyAsync<Exception>(() => batch.FlushPendingUpdatesAsync());
        await fixture.StartPostgresAsync();

        // At this point one debit committed and the other claim is still durable in Redis.
        await fixture.AssertAccountedForAsync(first.GroupId, firstCost);
        await fixture.AssertAccountedForAsync(second.GroupId, secondCost);

        await using var recoveryBatch = fixture.CreateBatchService();
        (await recoveryBatch.FlushPendingUpdatesAsync()).Should().BeGreaterThan(0);
        await fixture.AssertAccountedForAsync(first.GroupId, firstCost);
        await fixture.AssertAccountedForAsync(second.GroupId, secondCost);

        // A second recovery pass must be a no-op and must not duplicate either debit.
        (await recoveryBatch.FlushPendingUpdatesAsync()).Should().Be(0);
        await fixture.AssertAccountedForAsync(first.GroupId, firstCost);
        await fixture.AssertAccountedForAsync(second.GroupId, secondCost);
    }
}
