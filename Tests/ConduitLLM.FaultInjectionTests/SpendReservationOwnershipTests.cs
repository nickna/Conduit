using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class SpendReservationOwnershipTests(BillingFaultFixture fixture)
{
    private static readonly DateTime BilledAt = new(2026, 10, 10, 12, 15, 0, DateTimeKind.Utc);

    [Theory(Timeout = 90_000)]
    [InlineData(0)]
    [InlineData(0.25)]
    public async Task SiblingKey_CannotMutateAnyLifecycleState_OriginalKeyOwnsUsage(decimal held)
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        const string request = "owned-request";
        (await batch.TryReserveSpendAsync(account.KeyId, held, request)).Should().BeTrue();
        await RejectSiblingAsync(batch, sibling, request, held);
        (await database.HashGetAsync($"spend_reservations:group:{account.GroupId}", request))
            .ToString().Should().Be(held.ToString(CultureInfo.InvariantCulture));
        (await batch.TryReserveSpendAsync(account.KeyId, held, request)).Should().BeTrue();
        (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request)).Should().BeTrue();
        await RejectSiblingAsync(batch, sibling, request, held);
        var known = held == 0m ? 0m : 0.05m;
        (await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, known, "missing usage", BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        await RejectSiblingAsync(batch, sibling, request, held);
        var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, request);
        evidence.Should().NotBeNull();
        evidence!.KnownAmount.Should().Be(known);
        evidence.HeldAmount.Should().Be(held - known);
        (await batch.ReconcileSpendReservationAsync(sibling, request, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "wrong key"))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.ReconcileSpendReservationAsync(account.KeyId, request, known,
            SpendReconciliationDisposition.AuthoritativeProviderCost, "operator", "provider statement"))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        await RejectSiblingAsync(batch, sibling, request, held);
        (await batch.SettleSpendReservationAsync(account.KeyId, request, known * 2, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await database.HashGetAsync($"spend_reservation_owners:group:{account.GroupId}", request))
            .ToString().Should().Be(account.KeyId.ToString(CultureInfo.InvariantCulture));
        var usagePrefix = $"key_usage_window_units:group:{account.GroupId}:window:2026101012:key:";
        (await database.KeyExistsAsync(usagePrefix + sibling)).Should().BeFalse();
        if (known > 0)
            (await database.StringGetAsync(usagePrefix + account.KeyId)).ToString().Should().Be("10000000");
        else
            (await database.KeyExistsAsync(usagePrefix + account.KeyId)).Should().BeFalse();
        await fixture.AssertAccountedForAsync(account.GroupId, known * 2);
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, known * 2);
    }

    [Fact(Timeout = 90_000)]
    public async Task CompetingKeys_OneOwnerWinsAtomicReservation_AndReleaseExpiryClearOwnership()
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        var owners = $"spend_reservation_owners:group:{account.GroupId}";
        for (var i = 0; i < 12; i++)
        {
            var request = $"race-{i}";
            var result = await Task.WhenAll(
                batch.TryReserveSpendAsync(account.KeyId, 0.25m, request),
                batch.TryReserveSpendAsync(sibling, 0.25m, request));
            result.Count(accepted => accepted).Should().Be(1);
            var owner = result[0] ? account.KeyId : sibling;
            var loser = result[0] ? sibling : account.KeyId;
            (await database.HashGetAsync(owners, request)).ToString().Should().Be(owner.ToString(CultureInfo.InvariantCulture));
            (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.25m);
            await batch.ReleaseSpendReservationAsync(loser, request);
            (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.25m);
            await batch.ReleaseSpendReservationAsync(owner, request);
            (await database.HashExistsAsync(owners, request)).Should().BeFalse();
            (await batch.TryReserveSpendAsync(loser, 0m, request)).Should().BeTrue();
            await database.SortedSetAddAsync($"spend_reservation_expiry:group:{account.GroupId}", request, 0);
            (await batch.TryReserveSpendAsync(owner, 0m, $"cleanup-{i}")).Should().BeTrue();
            (await database.HashExistsAsync(owners, request)).Should().BeFalse();
            (await batch.TryReserveSpendAsync(owner, 0m, request)).Should().BeTrue();
            await batch.ReleaseSpendReservationAsync(owner, request);
            await batch.ReleaseSpendReservationAsync(owner, $"cleanup-{i}");
        }
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
    }

    [Fact(Timeout = 90_000)]
    public async Task StartedOwner_PersistsAcrossServiceRestart_AndSettlesOnlyToOriginalKey()
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        const string request = "recovered-started-owner";
        await using (var initial = await fixture.StartBatchServiceAsync())
        {
            (await initial.TryReserveSpendAsync(account.KeyId, 0.25m, request)).Should().BeTrue();
            (await initial.MarkSpendReservationInvocationStartedAsync(account.KeyId, request)).Should().BeTrue();
        }
        await using var recovered = await fixture.StartBatchServiceAsync();
        (await recovered.MarkSpendReservationInvocationStartedAsync(sibling, request)).Should().BeFalse();
        (await recovered.SettleSpendReservationAsync(sibling, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await recovered.SettleSpendReservationAsync(account.KeyId, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        (await recovered.SettleSpendReservationAsync(account.KeyId, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        await recovered.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
    }

    [Theory(Timeout = 90_000)]
    [InlineData(0, false)]
    [InlineData(0.25, false)]
    [InlineData(0.25, true)]
    public async Task LegacyBareHold_FailsClosed_UntilAuditedRecovery(decimal held, bool started)
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        const string request = "legacy-bare";
        (await batch.TryReserveSpendAsync(account.KeyId, held, request)).Should().BeTrue();
        if (started) (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request)).Should().BeTrue();
        await database.HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", request);
        foreach (var key in new[] { account.KeyId, sibling })
        {
            (await batch.TryReserveSpendAsync(key, held, request)).Should().BeFalse();
            (await batch.MarkSpendReservationInvocationStartedAsync(key, request)).Should().BeFalse();
            (await batch.SettleSpendReservationAsync(key, request, 0m)).Status.Should().Be(SpendReservationSettlementStatus.Conflict);
            (await batch.RecordIndeterminateSpendReservationAsync(key, request, 0m, "unverified"))
                .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
            await batch.ReleaseSpendReservationAsync(key, request);
        }
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(held);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "request log verified"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Recovered);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "request log verified"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.AlreadyRecovered);
        (await batch.RecoverLegacySpendReservationOwnerAsync(sibling, request, "operator", "wrong key"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Conflict);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "different evidence"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Conflict);
        await using (var recovered = fixture.CreateBatchService())
        {
            (await recovered.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "request log verified"))
                .Should().Be(SpendReservationOwnerRecoveryStatus.AlreadyRecovered);
            (await recovered.MarkSpendReservationInvocationStartedAsync(sibling, request)).Should().BeFalse();
        }
        var state = started ? "spend_reservations_started" : "spend_reservations";
        (await database.HashGetAsync($"{state}:group:{account.GroupId}", request)).ToString()
            .Should().Be(held.ToString(CultureInfo.InvariantCulture));
        using var audit = JsonDocument.Parse((await database.HashGetAsync(
            $"spend_reservation_owner_recovery:group:{account.GroupId}", request)).ToString());
        audit.RootElement.GetProperty("auditActor").GetString().Should().Be("operator");
        audit.RootElement.GetProperty("auditNote").GetString().Should().Be("request log verified");
        audit.RootElement.GetProperty("virtualKeyId").GetString().Should().Be(account.KeyId.ToString(CultureInfo.InvariantCulture));
        audit.RootElement.GetProperty("observedAtUtc").GetString().Should().NotBeNullOrEmpty();
        (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request)).Should().BeTrue();
        (await batch.SettleSpendReservationAsync(account.KeyId, request, 0m)).Status.Should().Be(SpendReservationSettlementStatus.Settled);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
    }

    [Fact(Timeout = 90_000)]
    public async Task LegacyReconciliation_RecoveryCannotReplaceRecordedIdentity_OrRequeueSettledSpend()
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        const string request = "legacy-reconciliation";
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request);
        await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
        await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.05m, "missing usage", BilledAt);
        await database.HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", request);
        (await batch.GetSpendReservationReconciliationAsync(account.KeyId, request)).Should().BeNull();
        (await batch.ReconcileSpendReservationAsync(account.KeyId, request, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "waived"))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.RecoverLegacySpendReservationOwnerAsync(sibling, request, "operator", "wrong key"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Conflict);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "record identity verified"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Recovered);
        (await batch.ReconcileSpendReservationAsync(account.KeyId, request, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "waived"))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        var auditKey = $"spend_reservation_owner_recovery:group:{account.GroupId}";
        var originalAudit = await database.HashGetAsync(auditKey, request);
        await database.HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", request);
        // Explicit recovery can restore the same owner without replacing immutable audit evidence.
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "record identity verified"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Recovered);
        (await database.HashGetAsync(auditKey, request)).Should().Be(originalAudit);
        (await batch.SettleSpendReservationAsync(account.KeyId, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.05m);
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
    }

    [Fact(Timeout = 90_000)]
    public async Task LegacySettledReservation_AuditedBindingRestoresReplayWithoutAnotherDebit()
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        const string request = "legacy-settled";
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request);
        await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
        await batch.SettleSpendReservationAsync(account.KeyId, request, 0.05m, BilledAt);
        await batch.FlushPendingUpdatesAsync();
        await redis.GetDatabase().HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", request);
        (await batch.SettleSpendReservationAsync(account.KeyId, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, request, "operator", "ledger and request verified"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Recovered);
        (await batch.SettleSpendReservationAsync(account.KeyId, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await batch.SettleSpendReservationAsync(sibling, request, 0.05m, BilledAt))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(0);
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
    }

    [Fact(Timeout = 90_000)]
    public async Task AuditedRecovery_ValidatesEvidenceAndLifetime_CompetingOwnersCannotBothBind()
    {
        var account = await fixture.SeedAccountAsync();
        var sibling = await AddSiblingAsync(account.GroupId);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        await Assert.ThrowsAsync<ArgumentException>(() => batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, "", "actor", "note"));
        await Assert.ThrowsAsync<ArgumentException>(() => batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, "id", "", "note"));
        await Assert.ThrowsAsync<ArgumentException>(() => batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, "id", "actor", ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => batch.RecoverLegacySpendReservationOwnerAsync(0, "id", "actor", "note"));
        (await batch.RecoverLegacySpendReservationOwnerAsync(int.MaxValue, "id", "actor", "note"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Missing);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, "missing", "actor", "note"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Missing);
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, "expired");
        await database.HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", "expired");
        await database.SortedSetAddAsync($"spend_reservation_expiry:group:{account.GroupId}", "expired", 0);
        (await batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, "expired", "actor", "note"))
            .Should().Be(SpendReservationOwnerRecoveryStatus.Missing);
        await batch.TryReserveSpendAsync(account.KeyId, 0m, "race");
        await database.HashDeleteAsync($"spend_reservation_owners:group:{account.GroupId}", "race");
        var results = await Task.WhenAll(
            batch.RecoverLegacySpendReservationOwnerAsync(account.KeyId, "race", "actor", "first evidence"),
            batch.RecoverLegacySpendReservationOwnerAsync(sibling, "race", "actor", "second evidence"));
        results.Count(result => result == SpendReservationOwnerRecoveryStatus.Recovered).Should().Be(1);
        results.Count(result => result == SpendReservationOwnerRecoveryStatus.Conflict).Should().Be(1);
    }

    private async Task<int> AddSiblingAsync(int groupId)
    {
        await using var db = fixture.CreateDbContext();
        var key = new VirtualKey { KeyName = "sibling", KeyHash = Guid.NewGuid().ToString("N"), VirtualKeyGroupId = groupId };
        db.Add(key);
        await db.SaveChangesAsync();
        return key.Id;
    }

    private static async Task RejectSiblingAsync(BatchSpendUpdateService batch, int keyId, string request, decimal held)
    {
        (await batch.TryReserveSpendAsync(keyId, held, request)).Should().BeFalse();
        (await batch.MarkSpendReservationInvocationStartedAsync(keyId, request)).Should().BeFalse();
        (await batch.SettleSpendReservationAsync(keyId, request, 0m)).Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.RecordIndeterminateSpendReservationAsync(keyId, request, 0m, "wrong key"))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.GetSpendReservationReconciliationAsync(keyId, request)).Should().BeNull();
        await batch.ReleaseSpendReservationAsync(keyId, request);
    }
}
