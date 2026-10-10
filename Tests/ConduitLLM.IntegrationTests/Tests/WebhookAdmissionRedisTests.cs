using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class WebhookAdmissionRedisTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task DistributedCircuit_OneProbeAcrossInstances_ExpiredOwnerRecovers_StaleSuccessIsFenced()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var options = Options.Create(new WebhookDeliveryOptions { AttemptTimeoutSeconds = 1, ConnectTimeoutSeconds = 1,
            CircuitFailureThreshold = 1, CircuitOpenSeconds = 1 });
        WebhookAdmission Create() => new(options, NullLogger<WebhookAdmission>.Instance, redis);
        var a = Create(); var b = Create();
        var url = $"https://receiver.test/{Guid.NewGuid():N}";
        var old = await a.AcquireAsync(url);
        var failure = await b.AcquireAsync(url);
        await failure.Lease!.RecordAsync(false);
        await old.Lease!.RecordAsync(true);
        Assert.Null((await a.AcquireAsync(url)).Lease);
        await Task.Delay(1200);
        var candidates = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => (i % 2 == 0 ? a : b).AcquireAsync(url)));
        var probe = Assert.Single(candidates, candidate => candidate.Lease != null);
        Assert.True(probe.IsProbe);
        // Do not release the owner. Its 31-second lease models process termination.
        await Task.Delay(TimeSpan.FromSeconds(32));
        var replacement = await b.AcquireAsync(url);
        Assert.True(replacement.IsProbe);
        Assert.NotNull(replacement.Lease);
        await probe.Lease!.RecordAsync(false);
        await replacement.Lease!.RecordAsync(true);
        var closed = await a.AcquireAsync(url);
        Assert.NotNull(closed.Lease); Assert.False(closed.IsProbe);
        await closed.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task DistributedAdmission_EnforcesAggregateDestinationLimit()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var options = Options.Create(new WebhookDeliveryOptions { GlobalConcurrency = 8, DestinationConcurrency = 2 });
        var a = new WebhookAdmission(options, NullLogger<WebhookAdmission>.Instance, redis);
        var b = new WebhookAdmission(options, NullLogger<WebhookAdmission>.Instance, redis);
        var url = $"https://receiver.test/{Guid.NewGuid():N}";
        var candidates = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => (i % 2 == 0 ? a : b).AcquireAsync(url)));
        Assert.Equal(2, candidates.Count(c => c.Lease != null));
        foreach (var candidate in candidates) if (candidate.Lease != null) await candidate.Lease.DisposeAsync();
    }
}
