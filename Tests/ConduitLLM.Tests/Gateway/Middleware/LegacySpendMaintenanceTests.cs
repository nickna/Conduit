using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Gateway.Middleware;

public sealed class LegacySpendMaintenanceTests
{
    private const int KeyId = 41;
    private const int GroupId = 7;
    private const decimal Cost = 2.75m;
    private const string KeyHash = "legacy-spend-key";
    private static readonly DateTime BilledAtUtc = new(2026, 10, 11, 4, 37, 49, DateTimeKind.Utc);
    private static readonly DateTime BillingWindowUtc = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("direct", "false")]
    [InlineData("direct", "exception")]
    [InlineData("direct", "cancellation")]
    [InlineData("cached", "exception")]
    [InlineData("cached", "cancellation")]
    public async Task UpdateSpendAsync_WhenPostDebitMaintenanceFails_AcceptsSpendWithoutFallback(
        string profile, string failure)
    {
        var fixture = new SpendFixture(profile);
        fixture.FailMaintenance(failure);

        await fixture.UpdateThroughHelperAsync();

        Assert.Equal(1, fixture.CommittedDebits);
        Assert.StartsWith("spend:", Assert.Single(fixture.AttemptedIdempotencyKeys));
        fixture.VerifyMaintenanceAttempted();
        fixture.VerifyNoFallback();
        fixture.VerifyNoNonIdempotentDebit();
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("cached", false)]
    [InlineData("cached", true)]
    public async Task UpdateSpendAsync_WhenDebitFails_QueuesOriginalIdentityAndReportsBillingFailure(
        string profile, bool cancellation)
    {
        var fixture = new SpendFixture(profile);
        fixture.ConfigureDebit(cancellation
            ? new OperationCanceledException("Debit was canceled before commit")
            : new InvalidOperationException("Database unavailable before commit"));

        var error = await Assert.ThrowsAsync<BillingSystemException>(fixture.UpdateThroughHelperAsync);

        Assert.Equal(KeyId, error.VirtualKeyId);
        Assert.Equal("database_update_failed", error.ErrorCode);
        Assert.Equal(0, fixture.CommittedDebits);
        var originalIdempotencyKey = Assert.Single(fixture.AttemptedIdempotencyKeys);
        Assert.StartsWith("spend:", originalIdempotencyKey);
        fixture.Batch.Verify(service => service.QueueFallbackUpdate(
            KeyId, Cost, BilledAtUtc, originalIdempotencyKey), Times.Once);
        fixture.Batch.Verify(service => service.QueueFallbackUpdate(
            It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
        fixture.Keys.Verify(repository => repository.UpdateAsync(
            It.IsAny<VirtualKey>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Cache.Verify(cache => cache.InvalidateVirtualKeyAsync(It.IsAny<string>()), Times.Never);
        fixture.VerifyNoNonIdempotentDebit();
    }

    [Theory]
    [InlineData("direct", "key")]
    [InlineData("direct", "group")]
    [InlineData("cached", "key")]
    [InlineData("cached", "group")]
    public async Task UpdateSpendAsync_WhenKeyOrGroupIsMissing_QueuesFallbackWithoutDebiting(
        string profile, string missingEntity)
    {
        var fixture = new SpendFixture(profile);
        if (missingEntity == "key")
        {
            fixture.Keys.Setup(repository => repository.GetByIdAsync(KeyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((VirtualKey)null);
        }
        else
        {
            fixture.Groups.Setup(repository => repository.GetByIdAsync(GroupId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(null);
            fixture.Groups.Setup(repository => repository.GetByKeyIdAsync(KeyId))
                .ReturnsAsync(null);
        }

        await Assert.ThrowsAsync<BillingSystemException>(fixture.UpdateThroughHelperAsync);

        Assert.Empty(fixture.AttemptedIdempotencyKeys);
        fixture.Batch.Verify(service => service.QueueFallbackUpdate(
            KeyId, Cost, BilledAtUtc, It.Is<string>(key => key.StartsWith("spend:"))), Times.Once);
        fixture.VerifyNoNonIdempotentDebit();
    }

    [Fact]
    public async Task UpdateSpendAsync_WhenCachedPublishAndCacheMaintenanceFail_RetainsEventDebitIdentity()
    {
        var fixture = new SpendFixture("cached");
        var events = new Mock<IEventBus>();
        SpendUpdateRequested published = null;
        events.Setup(bus => bus.PublishAsync(It.IsAny<SpendUpdateRequested>(), It.IsAny<CancellationToken>()))
            .Callback<SpendUpdateRequested, CancellationToken>((request, _) => published = request)
            .ThrowsAsync(new InvalidOperationException("Publish acknowledgement lost"));
        fixture.Service = fixture.CreateCachedService(events.Object);
        fixture.FailMaintenance("exception");

        await fixture.UpdateThroughHelperAsync();

        Assert.NotNull(published);
        Assert.False(string.IsNullOrWhiteSpace(published.RequestId));
        Assert.Equal(BilledAtUtc, published.BilledAtUtc);
        Assert.Equal(SpendIdempotency.KeyFor(published.RequestId),
            Assert.Single(fixture.AttemptedIdempotencyKeys));
        Assert.Equal(1, fixture.CommittedDebits);
        fixture.VerifyMaintenanceAttempted();
        fixture.VerifyNoFallback();
        fixture.VerifyNoNonIdempotentDebit();
    }

    private sealed class SpendFixture
    {
        private readonly string _profile;
        private readonly VirtualKey _key = new()
        {
            Id = KeyId, KeyHash = KeyHash, VirtualKeyGroupId = GroupId
        };

        public Mock<IBatchSpendUpdateService> Batch { get; } = new();
        public Mock<IVirtualKeyRepository> Keys { get; } = new();
        public Mock<IVirtualKeyGroupRepository> Groups { get; } = new();
        public Mock<IVirtualKeyCache> Cache { get; } = new();
        public List<string> AttemptedIdempotencyKeys { get; } = [];
        public IVirtualKeyRuntimeService Service { get; set; }
        public int CommittedDebits { get; private set; }

        public SpendFixture(string profile)
        {
            _profile = profile;
            var group = new VirtualKeyGroup { Id = GroupId, Balance = 50m };
            Batch.Setup(service => service.IsHealthy).Returns(false);
            Keys.Setup(repository => repository.GetByIdAsync(KeyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(_key);
            Keys.Setup(repository => repository.UpdateAsync(_key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Groups.Setup(repository => repository.GetByIdAsync(GroupId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(group);
            Groups.Setup(repository => repository.GetByKeyIdAsync(KeyId)).ReturnsAsync(group);
            Cache.Setup(cache => cache.InvalidateVirtualKeyAsync(KeyHash)).Returns(Task.CompletedTask);
            ConfigureDebit();
            Service = profile == "direct"
                ? new DirectApiVirtualKeyService(
                    Keys.Object, Groups.Object, Mock.Of<IVirtualKeySpendHistoryRepository>(),
                    null, Mock.Of<ILogger<DirectApiVirtualKeyService>>())
                : CreateCachedService();
        }

        public CachedApiVirtualKeyService CreateCachedService(IEventBus events = null) => new(
            Keys.Object, Mock.Of<IVirtualKeySpendHistoryRepository>(), Groups.Object,
            Cache.Object, events, Mock.Of<ILogger<CachedApiVirtualKeyService>>());

        public Task UpdateThroughHelperAsync() => SpendUpdateHelper.UpdateSpendAsync(
            KeyId, Cost, Batch.Object, Service, Mock.Of<ILogger>(), BilledAtUtc);

        public void ConfigureDebit(Exception failure = null)
        {
            Groups.Setup(repository => repository.AdjustBalanceIdempotentAsync(
                    GroupId, -Cost, It.IsAny<string>(), It.IsAny<string>(), "System",
                    ReferenceType.VirtualKey, "41", BillingWindowUtc))
                .Callback<int, decimal, string, string, string, ReferenceType, string, DateTime>(
                    (_, _, idempotencyKey, _, _, _, _, _) => AttemptedIdempotencyKeys.Add(idempotencyKey))
                .Returns(() =>
                {
                    if (failure != null)
                    {
                        return Task.FromException<BalanceAdjustmentResult>(failure);
                    }

                    CommittedDebits++;
                    return Task.FromResult(new BalanceAdjustmentResult(47.25m, Cost, true));
                });
        }

        public void FailMaintenance(string failure)
        {
            Exception error = failure == "cancellation"
                ? new OperationCanceledException("Maintenance canceled after debit committed")
                : new InvalidOperationException("Maintenance failed after debit committed");
            if (_profile == "direct")
            {
                var update = Keys.Setup(repository => repository.UpdateAsync(_key, It.IsAny<CancellationToken>()));
                if (failure == "false")
                {
                    update.ReturnsAsync(false);
                }
                else
                {
                    update.ThrowsAsync(error);
                }
            }
            else
            {
                Cache.Setup(cache => cache.InvalidateVirtualKeyAsync(KeyHash)).ThrowsAsync(error);
            }
        }

        public void VerifyMaintenanceAttempted()
        {
            if (_profile == "direct")
            {
                Keys.Verify(repository => repository.UpdateAsync(_key, It.IsAny<CancellationToken>()), Times.Once);
            }
            else
            {
                Cache.Verify(cache => cache.InvalidateVirtualKeyAsync(KeyHash), Times.Once);
            }
        }

        public void VerifyNoFallback()
        {
            Batch.Verify(service => service.QueueFallbackUpdate(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>(), It.IsAny<string>()), Times.Never);
            Batch.Verify(service => service.QueueFallbackUpdate(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
        }

        public void VerifyNoNonIdempotentDebit() => Groups.Verify(repository => repository.AdjustBalanceAsync(
            It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ReferenceType>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }
}
