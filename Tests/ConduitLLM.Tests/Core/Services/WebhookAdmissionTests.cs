using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Services;

public sealed class WebhookAdmissionTests
{
    [Fact]
    public async Task RedisOutage_UsesLocalAdmissionWithoutLosingWorkOrExceedingLocalLimit()
    {
        var database = new Mock<IDatabase>();
        database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline"));
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var admission = new WebhookAdmission(Options.Create(new WebhookDeliveryOptions { GlobalConcurrency = 2, DestinationConcurrency = 1 }),
            NullLogger<WebhookAdmission>.Instance, redis.Object);
        var lease = (await admission.AcquireAsync("receiver")).Lease;
        Assert.NotNull(lease);
        Assert.Null((await admission.AcquireAsync("receiver")).Lease);
        await lease.RecordAsync(true);
        Assert.NotNull((await admission.AcquireAsync("receiver")).Lease);
    }

    [Fact]
    public async Task LocalAdmission_BoundsDestinationsAndLeavesCapacityForHealthyReceiver()
    {
        var clock = new Clock();
        var admission = Create(clock, global: 3, destination: 2);
        var first = await admission.AcquireAsync("noisy");
        var second = await admission.AcquireAsync("noisy");
        var capacityDenied = await admission.AcquireAsync("noisy");
        Assert.Null(capacityDenied.Lease);
        Assert.Equal(clock.Now.UtcDateTime.AddMilliseconds(250), capacityDenied.DueAt);
        var healthy = await admission.AcquireAsync("healthy");
        Assert.NotNull(healthy.Lease);
        Assert.Null((await admission.AcquireAsync("third")).Lease);
        await first.Lease!.DisposeAsync(); await second.Lease!.DisposeAsync(); await healthy.Lease!.DisposeAsync();
        Assert.NotNull((await admission.AcquireAsync("third")).Lease);
    }

    [Fact]
    public async Task LocalAdmission_LeasesRecoverAndStaleResultsCannotCloseNewCircuit()
    {
        var clock = new Clock();
        var admission = Create(clock);
        var old = await admission.AcquireAsync("receiver");
        var failure = await admission.AcquireAsync("receiver");
        await failure.Lease!.RecordAsync(false);
        await old.Lease!.RecordAsync(true);
        Assert.Null((await admission.AcquireAsync("receiver")).Lease);
        clock.Now = clock.Now.AddSeconds(2);
        var probe = await admission.AcquireAsync("receiver");
        Assert.True(probe.IsProbe);
        Assert.Null((await admission.AcquireAsync("receiver")).Lease);
        clock.Now = clock.Now.AddSeconds(41); // Simulate a dead probe owner.
        var replacement = await admission.AcquireAsync("receiver");
        Assert.True(replacement.IsProbe);
        await probe.Lease!.RecordAsync(false); // Expired callback has no authority.
        await replacement.Lease!.RecordAsync(true);
        Assert.False((await admission.AcquireAsync("receiver")).IsProbe);
    }

    private static WebhookAdmission Create(Clock clock, int global = 4, int destination = 4) => new(
        Options.Create(new WebhookDeliveryOptions { GlobalConcurrency = global, DestinationConcurrency = destination,
            CircuitFailureThreshold = 1, CircuitOpenSeconds = 1 }), NullLogger<WebhookAdmission>.Instance, timeProvider: clock);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
