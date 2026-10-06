using ConduitLLM.Configuration;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services.SpendNotification;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "DistributedLock")]
public sealed class LockCallerRedisTests(PostgresLockTestContainerFixture fixture)
{
    private PostgresDistributedLockProvider Provider() => new(fixture.ConnectionString,
        NullLogger<PostgresDistributedLockProvider>.Instance);

    [Fact]
    public async Task ConcurrentAlerts_UseRealRedisMarkerAndCooldown_WithIsolatedSends()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        var repository = new SpendDataRepository(database, NullLogger<SpendDataRepository>.Instance);
        var keyId = Random.Shared.Next(100_000_000, int.MaxValue);
        var marker = RedisKeys.Spend.SentAlert(keyId.ToString(), "50");
        var cooldown = RedisKeys.Spend.Cooldown(keyId.ToString(), "budget:50");
        var client = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(client.Object);
        var hub = new Mock<IHubContext<SpendNotificationHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        BudgetAlertManager Manager() => new(hub.Object, repository, Provider(), NullLogger<BudgetAlertManager>.Instance);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Manager().CheckBudgetThresholdsAsync(keyId, 50, 100, 50)));
            await Manager().CheckBudgetThresholdsAsync(keyId, 50, 100, 50);
            client.Verify(value => value.SendCoreAsync("BudgetAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.True(await repository.IsAlertSentAsync(keyId, 50));
            Assert.True(await repository.IsAlertInCooldownAsync(keyId, "budget:50"));
            Assert.InRange((await database.KeyTimeToLiveAsync(marker))!.Value.TotalHours, 23.9, 24);
            Assert.InRange((await database.KeyTimeToLiveAsync(cooldown))!.Value.TotalHours, 3.9, 4);
            // The idempotency marker still suppresses a send independently of the cooldown.
            await database.KeyDeleteAsync(cooldown);
            await Manager().CheckBudgetThresholdsAsync(keyId, 50, 100, 50);
            client.Verify(value => value.SendCoreAsync("BudgetAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await database.KeyDeleteAsync([marker, cooldown]); }
    }

    [Fact]
    public async Task LeaderAndFollower_WarmRealPostgres_PublishOneSignal_AndUnsubscribe()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        using var observer = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var factory = new GatedFactory(fixture.ConnectionString);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<ConduitDbContext>>(factory).BuildServiceProvider();
        var serviceType = $"LockFixture-{Guid.NewGuid():N}";
        var options = new ConnectionPoolWarmingOptions { SignalTimeout = TimeSpan.FromSeconds(5), StaggerDelay = TimeSpan.Zero };
        var channel = RedisChannel.Literal($"{options.WarmingSignalChannel}:{serviceType}");
        var subscriber = observer.GetSubscriber();
        var signals = 0;
        await subscriber.SubscribeAsync(channel, (_, _) => Interlocked.Increment(ref signals));
        using var leader = new CoordinatedConnectionPoolWarmer(services, Provider(), redis,
            NullLogger<CoordinatedConnectionPoolWarmer>.Instance, options, serviceType);
        using var follower = new CoordinatedConnectionPoolWarmer(services, Provider(), redis,
            NullLogger<CoordinatedConnectionPoolWarmer>.Instance, options, serviceType);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var lead = leader.StartAsync(timeout.Token);
        Task follow = Task.CompletedTask;
        try
        {
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            follow = follower.StartAsync(timeout.Token);
            await WaitForSubscriptionsAsync(redis, channel, 2);
            factory.Continue.TrySetResult();
            await Task.WhenAll(lead, follow).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(10, factory.Calls); // Five real EF connections per instance.
            await WaitForAsync(() => Volatile.Read(ref signals) == 1);
            await leader.StopAsync(CancellationToken.None);
            await follower.StopAsync(CancellationToken.None);
        }
        finally
        {
            factory.Continue.TrySetResult();
            timeout.Cancel();
            try { await Task.WhenAll(lead, follow); } catch (OperationCanceledException) { }
            await subscriber.UnsubscribeAsync(channel);
        }
        await WaitForSubscriptionsAsync(redis, channel, 0);
    }

    [Fact]
    public async Task WarmingOwnershipLoss_CancelsAllStartedWork_WithoutPublishingSuccess()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var factory = new GatedFactory(fixture.ConnectionString);
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<ConduitDbContext>>(factory).BuildServiceProvider();
        var serviceType = $"LockFixture-{Guid.NewGuid():N}";
        var options = new ConnectionPoolWarmingOptions();
        var channel = RedisChannel.Literal($"{options.WarmingSignalChannel}:{serviceType}");
        var subscriber = redis.GetSubscriber();
        var signals = 0;
        await subscriber.SubscribeAsync(channel, (_, _) => Interlocked.Increment(ref signals));
        using var warmer = new CoordinatedConnectionPoolWarmer(services, Provider(), redis,
            NullLogger<CoordinatedConnectionPoolWarmer>.Instance, options, serviceType);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var warm = warmer.StartAsync(shutdown.Token);
        try
        {
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await LockTestSession.TerminateAsync(fixture.ConnectionString, $"{options.WarmingLockKey}:{serviceType}");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warm.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref signals));
        }
        finally
        {
            shutdown.Cancel();
            factory.Continue.TrySetResult();
            try { await warm; } catch (OperationCanceledException) { }
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    private static async Task WaitForSubscriptionsAsync(ConnectionMultiplexer redis, RedisChannel channel, int expected)
    {
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var result = (RedisResult[])(await server.ExecuteAsync("PUBSUB", "NUMSUB", channel.ToString()))!;
            if ((int)result[1] == expected) { return; }
            await Task.Delay(10, bound.Token);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) { await Task.Delay(10, bound.Token); }
    }

    private sealed class GatedFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options);
        public async Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return CreateDbContext();
        }
    }
}
