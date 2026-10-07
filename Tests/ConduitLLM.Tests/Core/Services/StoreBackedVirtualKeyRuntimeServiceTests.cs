using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Core.Services;

public sealed class StoreBackedVirtualKeyRuntimeServiceTests
{
    private readonly Mock<IVirtualKeyRuntimeStore> _store = new();
    private readonly Mock<IBatchSpendUpdateService> _batchSpendService = new();
    private readonly StoreBackedVirtualKeyRuntimeService _service;

    public StoreBackedVirtualKeyRuntimeServiceTests()
    {
        _service = new StoreBackedVirtualKeyRuntimeService(
            _store.Object,
            Mock.Of<ILogger<StoreBackedVirtualKeyRuntimeService>>(),
            _batchSpendService.Object);
    }

    [Fact]
    public async Task Authentication_MapsCompleteRuntimeSnapshotWithoutReadingPendingSpend()
    {
        const string plaintextKey = "condt_native_runtime";
        var record = CreateRecord();
        _store.Setup(value => value.GetByHashAsync(
                ConduitLLM.Configuration.Utilities.VirtualKeyUtilities.HashKey(plaintextKey),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var outcome = await _service.ValidateVirtualKeyForAuthenticationAsync(
            plaintextKey,
            "gpt-5");

        Assert.True(outcome.IsValid);
        var key = Assert.IsType<ConduitLLM.Configuration.Entities.VirtualKey>(outcome.Key);
        Assert.Equal(record.Id, key.Id);
        Assert.Equal(record.KeyName, key.KeyName);
        Assert.Equal(record.KeyHash, key.KeyHash);
        Assert.Equal(record.Description, key.Description);
        Assert.Equal(record.VirtualKeyGroupId, key.VirtualKeyGroupId);
        Assert.Equal(record.Metadata, key.Metadata);
        Assert.Equal(record.AllowedModels, key.AllowedModels);
        Assert.Equal(record.RateLimitRpm, key.RateLimitRpm);
        Assert.Equal(record.RateLimitRpd, key.RateLimitRpd);
        Assert.Equal(record.RateLimitTpm, key.RateLimitTpm);
        Assert.Equal(record.MaxParallelRequests, key.MaxParallelRequests);
        Assert.Equal(record.RateLimitPriority, key.RateLimitPriority);
        Assert.Equal(record.ModelRateLimits, key.ModelRateLimits);
        Assert.Equal(record.RowVersion, key.RowVersion);

        var group = key.VirtualKeyGroup;
        Assert.Equal(record.Group.Id, group.Id);
        Assert.Equal(record.Group.ExternalGroupId, group.ExternalGroupId);
        Assert.Equal(record.Group.GroupName, group.GroupName);
        Assert.Equal(record.Group.Balance, group.Balance);
        Assert.Equal(record.Group.LifetimeCreditsAdded, group.LifetimeCreditsAdded);
        Assert.Equal(record.Group.LifetimeSpent, group.LifetimeSpent);
        Assert.Equal(record.Group.MediaRetentionPolicyId, group.MediaRetentionPolicyId);
        Assert.Equal(record.Group.RateLimitRpm, group.RateLimitRpm);
        Assert.Equal(record.Group.RateLimitRpd, group.RateLimitRpd);
        Assert.Equal(record.Group.RateLimitTpm, group.RateLimitTpm);
        Assert.Equal(record.Group.MaxParallelRequests, group.MaxParallelRequests);
        Assert.Equal(record.Group.RowVersion, group.RowVersion);
        Assert.Same(key, Assert.Single(group.VirtualKeys));
        _batchSpendService.Verify(
            value => value.GetPendingSpendAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task BalanceValidation_SubtractsPendingSpendFromPersistedGroupBalance()
    {
        const string plaintextKey = "condt_pending_native";
        var record = CreateRecord(groupBalance: 5m);
        _store.Setup(value => value.GetByHashAsync(
                ConduitLLM.Configuration.Utilities.VirtualKeyUtilities.HashKey(plaintextKey),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        _batchSpendService.Setup(value => value.GetPendingSpendAsync(record.Id))
            .ReturnsAsync(5m);

        var outcome = await _service.ValidateVirtualKeyAsync(plaintextKey);

        Assert.False(outcome.IsValid);
        Assert.Equal(VirtualKeyValidationFailureCodes.InsufficientBalance, outcome.FailureCode);
        Assert.Equal(402, outcome.HttpStatusCode);
        Assert.Equal(record.Id, outcome.Key?.Id);
        _batchSpendService.Verify(value => value.GetPendingSpendAsync(record.Id), Times.Once);
    }

    [Fact]
    public async Task BalanceValidation_DisabledKeyDoesNotReadPendingSpend()
    {
        const string plaintextKey = "condt_disabled_native";
        var record = CreateRecord(isEnabled: false);
        _store.Setup(value => value.GetByHashAsync(
                ConduitLLM.Configuration.Utilities.VirtualKeyUtilities.HashKey(plaintextKey),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var outcome = await _service.ValidateVirtualKeyAsync(plaintextKey);

        Assert.False(outcome.IsValid);
        Assert.Equal(VirtualKeyValidationFailureCodes.KeyDisabled, outcome.FailureCode);
        _batchSpendService.Verify(
            value => value.GetPendingSpendAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task Validation_StoreFailureReturnsStableTypedFailure()
    {
        _store.Setup(value => value.GetByHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var outcome = await _service.ValidateVirtualKeyAsync("condt_store_failure");

        Assert.False(outcome.IsValid);
        Assert.Equal(VirtualKeyValidationFailureCodes.ValidationError, outcome.FailureCode);
        Assert.Equal(500, outcome.HttpStatusCode);
    }

    [Fact]
    public async Task UpdateSpend_PersistsDebitLedgerIdentityAndTouchesKey()
    {
        var record = CreateRecord();
        VirtualKeyBalanceAdjustment? captured = null;
        _store.Setup(value => value.GetByIdAsync(record.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        _store.Setup(value => value.AdjustBalanceAsync(
                It.IsAny<VirtualKeyBalanceAdjustment>(),
                It.IsAny<CancellationToken>()))
            .Callback<VirtualKeyBalanceAdjustment, CancellationToken>((value, _) => captured = value)
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(46.75m, 13.25m, Applied: true));
        _store.Setup(value => value.TouchAsync(
                record.Id,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.UpdateSpendAsync(record.Id, 3.25m);

        Assert.True(result);
        var adjustment = Assert.IsType<VirtualKeyBalanceAdjustment>(captured);
        Assert.Equal(record.Group.Id, adjustment.GroupId);
        Assert.Equal(-3.25m, adjustment.Amount);
        Assert.Equal($"API usage by virtual key #{record.Id}", adjustment.Description);
        Assert.Equal("System", adjustment.InitiatedBy);
        Assert.Equal(VirtualKeyBalanceReferenceType.VirtualKey, adjustment.ReferenceType);
        Assert.Equal(record.Id.ToString(), adjustment.ReferenceId);
        Assert.Null(adjustment.IdempotencyKey);
        Assert.NotNull(adjustment.BillingWindowStartUtc);
        Assert.Equal(0, adjustment.BillingWindowStartUtc.Value.Minute);
        Assert.Equal(0, adjustment.BillingWindowStartUtc.Value.Second);
        _store.Verify(value => value.TouchAsync(
            record.Id,
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateSpend_TouchFailureAfterDurableDebitStillReportsSuccess()
    {
        var record = CreateRecord();
        _store.Setup(value => value.GetByIdAsync(record.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        _store.Setup(value => value.AdjustBalanceAsync(
                It.IsAny<VirtualKeyBalanceAdjustment>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(49m, 11m, Applied: true));
        _store.Setup(value => value.TouchAsync(
                record.Id,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("concurrent delete"));

        var result = await _service.UpdateSpendAsync(record.Id, 1m);

        Assert.True(result);
        _store.Verify(value => value.AdjustBalanceAsync(
            It.IsAny<VirtualKeyBalanceAdjustment>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateSpend_AdjustmentFailureReportsFalseAndDoesNotTouchKey()
    {
        var record = CreateRecord();
        _store.Setup(value => value.GetByIdAsync(record.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        _store.Setup(value => value.AdjustBalanceAsync(
                It.IsAny<VirtualKeyBalanceAdjustment>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("write failed"));

        var result = await _service.UpdateSpendAsync(record.Id, 1m);

        Assert.False(result);
        _store.Verify(value => value.TouchAsync(
            It.IsAny<int>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateSpend_NonPositiveCostDoesNotAccessStore()
    {
        Assert.True(await _service.UpdateSpendAsync(29, 0m));
        Assert.True(await _service.UpdateSpendAsync(29, -1m));

        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetVirtualKeyInfo_PropagatesCancellationAndReturnsHydratedGroup()
    {
        var record = CreateRecord();
        using var cancellation = new CancellationTokenSource();
        _store.Setup(value => value.GetByIdAsync(record.Id, cancellation.Token))
            .ReturnsAsync(record);

        var key = await _service.GetVirtualKeyInfoForValidationAsync(
            record.Id,
            cancellation.Token);

        Assert.NotNull(key);
        Assert.Equal(record.Group.Id, key.VirtualKeyGroup.Id);
        _store.Verify(value => value.GetByIdAsync(record.Id, cancellation.Token), Times.Once);
    }

    private static VirtualKeyRuntimeRecord CreateRecord(
        bool isEnabled = true,
        decimal groupBalance = 50m)
    {
        var now = DateTime.UtcNow;
        return new VirtualKeyRuntimeRecord
        {
            Id = 29,
            KeyName = "native-key",
            KeyHash = "hash-29",
            Description = "runtime key",
            IsEnabled = isEnabled,
            VirtualKeyGroupId = 7,
            ExpiresAt = now.AddDays(1),
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddMinutes(-1),
            Metadata = "{\"role\":\"test\"}",
            AllowedModels = "gpt-5,claude-*",
            RateLimitRpm = 101,
            RateLimitRpd = 202,
            RateLimitTpm = 303,
            MaxParallelRequests = 4,
            RateLimitPriority = 2,
            ModelRateLimits = "{\"gpt-5\":{\"rpm\":5}}",
            RowVersion = [1, 2, 3],
            Group = new VirtualKeyGroupRuntimeRecord
            {
                Id = 7,
                ExternalGroupId = "tenant-7",
                GroupName = "native-group",
                Balance = groupBalance,
                LifetimeCreditsAdded = 60m,
                LifetimeSpent = 10m,
                CreatedAt = now.AddDays(-10),
                UpdatedAt = now,
                MediaRetentionPolicyId = 8,
                RateLimitRpm = 1001,
                RateLimitRpd = 2002,
                RateLimitTpm = 3003,
                MaxParallelRequests = 40,
                RowVersion = [4, 5, 6]
            }
        };
    }
}
