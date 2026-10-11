using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.EventHandlers;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Messaging;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Gateway.Middleware;

public sealed class SpendTimestampAttributionTests
{
    private static readonly DateTime UsageUtc =
        new(2000, 1, 1, 2, 37, 49, 987, DateTimeKind.Utc);
    private static readonly DateTime WindowUtc =
        new(2000, 1, 1, 2, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> HelperCases()
    {
        foreach (var profile in new[] { "store", "direct", "cached" })
        foreach (var tier in new[] { "redis", "direct", "fallback", "redis-failure-direct", "redis-failure-fallback" })
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
        {
            yield return [profile, tier, kind];
        }
    }

    public static IEnumerable<object[]> RuntimeCases()
    {
        foreach (var profile in new[] { "store", "direct", "cached" })
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
        {
            yield return [profile, kind];
        }
    }

    [Theory]
    [MemberData(nameof(RuntimeCases))]
    public async Task UpdateSpendAsync_DirectRuntimeCall_NormalizesOriginalUsageHour(
        string profile, DateTimeKind kind)
    {
        var fixture = new SpendFixture(profile);
        fixture.ConfigureDebit(fails: false);

        Assert.True(await fixture.Service.UpdateSpendAsync(41, 2.75m, AsKind(kind)));

        Assert.Equal(WindowUtc, fixture.BillingWindow);
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(fixture.BillingWindow).Kind);
    }

    [Theory]
    [MemberData(nameof(HelperCases))]
    public async Task UpdateSpendAsync_PriorUsageTimestamp_PreservesUtcHourAcrossAllTiers(
        string profile, string tier, DateTimeKind kind)
    {
        var fixture = new SpendFixture(profile);
        var fallback = tier.EndsWith("fallback", StringComparison.Ordinal);
        var redisFails = tier.StartsWith("redis-failure", StringComparison.Ordinal);
        fixture.Batch.Setup(service => service.IsHealthy)
            .Returns(tier.StartsWith("redis", StringComparison.Ordinal));
        fixture.ConfigureDebit(fails: fallback);
        if (redisFails)
        {
            fixture.Batch.Setup(service => service.QueueSpendUpdateAsync(
                    41, 2.75m, It.IsAny<DateTime?>()))
                .ThrowsAsync(new InvalidOperationException("Redis unavailable"));
        }
        else
        {
            fixture.Batch.Setup(service => service.QueueSpendUpdateAsync(
                    41, 2.75m, It.IsAny<DateTime?>()))
                .Returns(Task.CompletedTask);
        }

        var update = () => SpendUpdateHelper.UpdateSpendAsync(
            41, 2.75m, fixture.Batch.Object, fixture.Service,
            Mock.Of<ILogger>(), AsKind(kind));

        if (fallback)
        {
            await Assert.ThrowsAsync<BillingSystemException>(update);
            fixture.Batch.Verify(service => service.QueueFallbackUpdate(
                41, 2.75m, It.Is<DateTime?>(time =>
                    time == UsageUtc && time.Value.Kind == DateTimeKind.Utc)), Times.Once);
        }
        else
        {
            await update();
            fixture.Batch.Verify(service => service.QueueFallbackUpdate(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
        }

        if (tier.StartsWith("redis", StringComparison.Ordinal))
        {
            fixture.Batch.Verify(service => service.QueueSpendUpdateAsync(
                41, 2.75m, It.Is<DateTime?>(time =>
                    time == UsageUtc && time.Value.Kind == DateTimeKind.Utc)), Times.Once);
        }

        if (tier == "redis")
        {
            Assert.Null(fixture.BillingWindow);
        }
        else
        {
            Assert.Equal(WindowUtc, fixture.BillingWindow);
            Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(fixture.BillingWindow).Kind);
        }
    }

    [Theory]
    [InlineData(true, DateTimeKind.Utc)]
    [InlineData(true, DateTimeKind.Local)]
    [InlineData(true, DateTimeKind.Unspecified)]
    [InlineData(false, DateTimeKind.Utc)]
    [InlineData(false, DateTimeKind.Local)]
    [InlineData(false, DateTimeKind.Unspecified)]
    public async Task HandleAsync_OriginalUsageTimestamp_PersistsUtcHourInBothProfiles(
        bool runtimeStore, DateTimeKind kind)
    {
        var fixture = new SpendFixture(runtimeStore ? "store" : "cached");
        fixture.ConfigureDebit(fails: false);
        var services = new ServiceCollection();
        if (runtimeStore)
        {
            services.AddSingleton(fixture.Store.Object);
        }
        else
        {
            services.AddSingleton(fixture.Keys.Object);
            services.AddSingleton(fixture.Groups.Object);
        }
        using var provider = services.BuildServiceProvider();
        var processor = new SpendUpdateProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(), Mock.Of<IEventBus>(),
            Mock.Of<ILogger<SpendUpdateProcessor>>());

        await processor.HandleAsync(new SpendUpdateRequested
        {
            KeyId = 41,
            Amount = 2.75m,
            RequestId = "original-usage",
            BilledAtUtc = AsKind(kind),
            Timestamp = UsageUtc.AddDays(10)
        }, new TestEventContext());

        Assert.Equal(WindowUtc, fixture.BillingWindow);
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(fixture.BillingWindow).Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_LegacyEventWithoutUsageTimestamp_UsesEventTimestamp(bool runtimeStore)
    {
        var fixture = new SpendFixture(runtimeStore ? "store" : "cached");
        fixture.ConfigureDebit(fails: false);
        var services = new ServiceCollection();
        if (runtimeStore)
        {
            services.AddSingleton(fixture.Store.Object);
        }
        else
        {
            services.AddSingleton(fixture.Keys.Object);
            services.AddSingleton(fixture.Groups.Object);
        }
        using var provider = services.BuildServiceProvider();
        var processor = new SpendUpdateProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(), Mock.Of<IEventBus>(),
            Mock.Of<ILogger<SpendUpdateProcessor>>());

        await processor.HandleAsync(new SpendUpdateRequested
        {
            KeyId = 41, Amount = 2.75m, RequestId = "legacy-usage", Timestamp = UsageUtc
        }, new TestEventContext());

        Assert.Equal(WindowUtc, fixture.BillingWindow);
    }

    [Theory]
    [InlineData(true, DateTimeKind.Utc)]
    [InlineData(true, DateTimeKind.Local)]
    [InlineData(true, DateTimeKind.Unspecified)]
    [InlineData(false, DateTimeKind.Utc)]
    [InlineData(false, DateTimeKind.Local)]
    [InlineData(false, DateTimeKind.Unspecified)]
    public async Task UpdateSpendAsync_CachedEventAndPublishFallback_RetainSameUsageTimestamp(
        bool publishSucceeds, DateTimeKind kind)
    {
        var fixture = new SpendFixture("cached");
        fixture.ConfigureDebit(fails: false);
        var events = new Mock<IEventBus>();
        SpendUpdateRequested published = null;
        events.Setup(bus => bus.PublishAsync(
                It.IsAny<SpendUpdateRequested>(), It.IsAny<CancellationToken>()))
            .Callback<SpendUpdateRequested, CancellationToken>((request, _) => published = request)
            .Returns(() => publishSucceeds
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("Publish failed")));
        var service = new CachedApiVirtualKeyService(
            fixture.Keys.Object, Mock.Of<IVirtualKeySpendHistoryRepository>(), fixture.Groups.Object,
            Mock.Of<IVirtualKeyCache>(), events.Object, Mock.Of<ILogger<CachedApiVirtualKeyService>>());

        Assert.True(await service.UpdateSpendAsync(41, 2.75m, AsKind(kind)));

        Assert.NotNull(published);
        Assert.Equal(UsageUtc, published.BilledAtUtc);
        Assert.Equal(DateTimeKind.Utc, published.BilledAtUtc.Value.Kind);
        if (publishSucceeds)
        {
            Assert.Null(fixture.BillingWindow);
        }
        else
        {
            Assert.Equal(WindowUtc, fixture.BillingWindow);
            fixture.Groups.Verify(group => group.AdjustBalanceIdempotentAsync(
                7, -2.75m, SpendIdempotency.KeyFor(published.RequestId),
                It.IsAny<string>(), "System", ReferenceType.VirtualKey, "41", WindowUtc), Times.Once);
        }
    }

    private static DateTime AsKind(DateTimeKind kind) => kind switch
    {
        DateTimeKind.Utc => UsageUtc,
        DateTimeKind.Local => UsageUtc.ToLocalTime(),
        _ => DateTime.SpecifyKind(UsageUtc.ToLocalTime(), DateTimeKind.Unspecified)
    };

    private sealed class SpendFixture
    {
        public Mock<IBatchSpendUpdateService> Batch { get; } = new();
        public Mock<IVirtualKeyRuntimeStore> Store { get; } = new();
        public Mock<IVirtualKeyRepository> Keys { get; } = new();
        public Mock<IVirtualKeyGroupRepository> Groups { get; } = new();
        public IVirtualKeyRuntimeService Service { get; }
        public DateTime? BillingWindow { get; private set; }

        public SpendFixture(string profile)
        {
            var key = new VirtualKey { Id = 41, KeyHash = "usage-key", VirtualKeyGroupId = 7 };
            var group = new VirtualKeyGroup { Id = 7, Balance = 50m };
            Keys.Setup(repository => repository.GetByIdAsync(41, It.IsAny<CancellationToken>()))
                .ReturnsAsync(key);
            Keys.Setup(repository => repository.UpdateAsync(key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Groups.Setup(repository => repository.GetByIdAsync(7, It.IsAny<CancellationToken>()))
                .ReturnsAsync(group);
            Groups.Setup(repository => repository.GetByKeyIdAsync(41)).ReturnsAsync(group);
            Store.Setup(store => store.GetByIdAsync(41, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKeyRuntimeRecord
                {
                    Id = 41, VirtualKeyGroupId = 7, KeyHash = "usage-key",
                    Group = new VirtualKeyGroupRuntimeRecord { Id = 7, Balance = 50m }
                });
            Store.Setup(store => store.TouchAsync(
                41, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Service = profile switch
            {
                "store" => new StoreBackedVirtualKeyRuntimeService(
                    Store.Object, Mock.Of<ILogger<StoreBackedVirtualKeyRuntimeService>>()),
                "direct" => new DirectApiVirtualKeyService(
                    Keys.Object, Groups.Object, Mock.Of<IVirtualKeySpendHistoryRepository>(),
                    null, Mock.Of<ILogger<DirectApiVirtualKeyService>>()),
                _ => new CachedApiVirtualKeyService(
                    Keys.Object, Mock.Of<IVirtualKeySpendHistoryRepository>(), Groups.Object,
                    Mock.Of<IVirtualKeyCache>(), null, Mock.Of<ILogger<CachedApiVirtualKeyService>>())
            };
        }

        public void ConfigureDebit(bool fails)
        {
            Store.Setup(store => store.AdjustBalanceAsync(
                    It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
                .Callback<VirtualKeyBalanceAdjustment, CancellationToken>(
                    (adjustment, _) => BillingWindow = adjustment.BillingWindowStartUtc)
                .Returns(() => fails
                    ? Task.FromException<VirtualKeyBalanceAdjustmentResult>(new InvalidOperationException("Debit failed"))
                    : Task.FromResult(new VirtualKeyBalanceAdjustmentResult(47.25m, 2.75m, true)));
            Groups.Setup(repository => repository.AdjustBalanceAsync(
                    7, -2.75m, It.IsAny<string>(), "System", ReferenceType.VirtualKey, "41", It.IsAny<DateTime>()))
                .Callback<int, decimal, string, string, ReferenceType, string, DateTime>(
                    (_, _, _, _, _, _, window) => BillingWindow = window)
                .Returns(() => fails
                    ? Task.FromException<decimal>(new InvalidOperationException("Debit failed"))
                    : Task.FromResult(47.25m));
            Groups.Setup(repository => repository.AdjustBalanceIdempotentAsync(
                    7, -2.75m, It.IsAny<string>(), It.IsAny<string>(), "System",
                    ReferenceType.VirtualKey, "41", It.IsAny<DateTime>()))
                .Callback<int, decimal, string, string, string, ReferenceType, string, DateTime>(
                    (_, _, _, _, _, _, _, window) => BillingWindow = window)
                .Returns(() => fails
                    ? Task.FromException<BalanceAdjustmentResult>(new InvalidOperationException("Debit failed"))
                    : Task.FromResult(new BalanceAdjustmentResult(47.25m, 2.75m, true)));
        }
    }
}
