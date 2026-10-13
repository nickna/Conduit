using AwesomeAssertions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class SpendReservationBoundReplayTests(BillingFaultFixture fixture)
{
    [Fact(Timeout = 90_000)]
    public async Task ChangedBoundReplay_RejectsAdmissionAndPreservesOriginalHoldAndAvailableBalance()
    {
        var account = await fixture.SeedAccountAsync(0.30m);
        await using var batch = await fixture.StartBatchServiceAsync();
        var service = new SpendReservationService(batch, NullLogger<SpendReservationService>.Instance);
        const string request = "bound-replay";
        (await service.ReserveAsync(account.KeyId, request, 0.25m)).Outcome.Should().Be(SpendReservationOutcome.Reserved);
        (await service.ReserveAsync(account.KeyId, request, 0.50m)).Outcome.Should().Be(SpendReservationOutcome.InsufficientBalance);
        (await service.ReserveAsync(account.KeyId, request, 0.20m)).Outcome.Should().Be(SpendReservationOutcome.InsufficientBalance);
        var replay = await service.ReserveAsync(account.KeyId, request, 0.25m);
        replay.Outcome.Should().Be(SpendReservationOutcome.Reserved);
        replay.ReservedAmount.Should().Be(0.25m);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        (await database.HashGetAsync($"spend_reservations:group:{account.GroupId}", request)).ToString().Should().Be("0.25");
        (await database.HashGetAsync($"spend_reservation_bounds:group:{account.GroupId}", request)).ToString().Should().Be("0.25");
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.25m);
        (await service.ReserveAsync(account.KeyId, "independent-request", 0.06m)).Outcome.Should().Be(SpendReservationOutcome.InsufficientBalance);
        await service.MarkInvocationStartedAsync(account.KeyId, request);
        (await service.SettleAsync(account.KeyId, request, 0.25m)).Status.Should().Be(SpendReservationSettlementStatus.Settled);
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.25m);
        await using var db = fixture.CreateDbContext();
        (await db.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId)).Balance.Should().Be(0.05m);
    }

    [Fact(Timeout = 90_000)]
    public async Task OriginalBound_IsCanonicalAndExact_EquivalentDecimalScalesReplay()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var bounds = $"spend_reservation_bounds:group:{account.GroupId}";
        (await batch.TryReserveSpendAsync(account.KeyId, 0.25000m, "scaled")).Should().BeTrue();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.25m, "scaled")).Should().BeTrue();
        (await redis.GetDatabase().HashGetAsync(bounds, "scaled")).ToString().Should().Be("0.25");
        // Redis's floating amount representation cannot distinguish these decimals;
        // immutable replay metadata must compare the exact requested maximum.
        (await batch.TryReserveSpendAsync(account.KeyId, 0.250000000000000001m, "precise")).Should().BeTrue();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.250000000000000002m, "precise")).Should().BeFalse();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.2500000000000000010m, "precise")).Should().BeTrue();
        (await redis.GetDatabase().HashGetAsync(bounds, "precise")).ToString().Should().Be("0.250000000000000001");
    }

    [Fact(Timeout = 90_000)]
    public async Task OriginalBound_PersistsAcrossStartedReducedIndeterminateAndSettledStatesAndRestart()
    {
        var account = await fixture.SeedAccountAsync();
        const string request = "reduced-hold";
        await using (var initial = await fixture.StartBatchServiceAsync())
        {
            await initial.TryReserveSpendAsync(account.KeyId, 0.25m, request);
            await initial.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
            (await initial.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeTrue();
            (await initial.TryReserveSpendAsync(account.KeyId, 0.20m, request)).Should().BeFalse();
            await initial.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.05m, "usage missing");
            (await initial.GetSpendReservationReconciliationAsync(account.KeyId, request))!.HeldAmount.Should().Be(0.20m);
            (await initial.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeTrue();
            (await initial.TryReserveSpendAsync(account.KeyId, 0.20m, request)).Should().BeFalse();
        }
        await using var recovered = await fixture.StartBatchServiceAsync();
        (await recovered.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeTrue();
        (await recovered.TryReserveSpendAsync(account.KeyId, 0.50m, request)).Should().BeFalse();
        (await recovered.ReconcileSpendReservationAsync(account.KeyId, request, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "waiver verified"))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        (await recovered.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeTrue();
        (await recovered.TryReserveSpendAsync(account.KeyId, 0.05m, request)).Should().BeFalse();
        await recovered.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
    }

    [Fact(Timeout = 90_000)]
    public async Task ReleaseAndExpiry_ClearOriginalBound_ZeroMaximumIsARealBound()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        var bounds = $"spend_reservation_bounds:group:{account.GroupId}";
        await batch.TryReserveSpendAsync(account.KeyId, 0m, "free");
        (await database.HashGetAsync(bounds, "free")).ToString().Should().Be("0");
        (await batch.TryReserveSpendAsync(account.KeyId, 0.01m, "free")).Should().BeFalse();
        await batch.ReleaseSpendReservationAsync(account.KeyId, "free");
        (await database.HashExistsAsync(bounds, "free")).Should().BeFalse();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.01m, "free")).Should().BeTrue();
        await batch.ReleaseSpendReservationAsync(account.KeyId, "free");
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, "expired");
        await database.SortedSetAddAsync($"spend_reservation_expiry:group:{account.GroupId}", "expired", 0);
        await batch.TryReserveSpendAsync(account.KeyId, 0m, "cleanup");
        (await database.HashExistsAsync(bounds, "expired")).Should().BeFalse();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.50m, "expired")).Should().BeTrue();
        (await database.HashGetAsync(bounds, "expired")).ToString().Should().Be("0.5");
        await batch.ReleaseSpendReservationAsync(account.KeyId, "expired");
        await batch.ReleaseSpendReservationAsync(account.KeyId, "cleanup");
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
    }

    [Fact(Timeout = 90_000)]
    public async Task ConcurrentDifferentBounds_OnlyOneMaximumIsAccepted()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        for (var i = 0; i < 12; i++)
        {
            var request = $"bound-race-{i}";
            var accepted = await Task.WhenAll(
                batch.TryReserveSpendAsync(account.KeyId, 0.25m, request),
                batch.TryReserveSpendAsync(account.KeyId, 0.50m, request));
            accepted.Count(value => value).Should().Be(1);
            var expected = accepted[0] ? 0.25m : 0.50m;
            (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(expected);
            (await batch.TryReserveSpendAsync(account.KeyId, expected, request)).Should().BeTrue();
            await batch.ReleaseSpendReservationAsync(account.KeyId, request);
        }
    }

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUnknownBound_RejectsReserveReplay_AccountingRemainsUsable(bool indeterminate)
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        const string request = "unknown-legacy-bound";
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request);
        await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
        if (indeterminate)
            await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.05m, "missing usage");
        await database.HashDeleteAsync($"spend_reservation_bounds:group:{account.GroupId}", request);
        await database.HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", request);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "request owner verified"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Recovered);
        (await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeFalse();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.20m, request)).Should().BeFalse();
        (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request)).Should().BeTrue();
        if (indeterminate)
            (await batch.ReconcileSpendReservationAsync(account.KeyId, request, 0m,
                SpendReconciliationDisposition.ProviderCostWaived, "operator", "legacy waived"))
                .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        else
            (await batch.SettleSpendReservationAsync(account.KeyId, request, 0.05m))
                .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        (await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeFalse();
        (await batch.SettleSpendReservationAsync(account.KeyId, request, 0.05m))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
    }
}
