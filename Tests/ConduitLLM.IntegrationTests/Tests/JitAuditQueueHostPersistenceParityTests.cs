using ConduitLLM.IntegrationTests.Infrastructure;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Partial JIT audit hosts exercise real HTTP admission, production audit DI and EF writes.
/// They do not stand in for the full Gateway middleware or Redis leader-election protocol.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class JitAuditQueueHostPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task TwoNeverLeaderHostsFlushBothSubthresholdQueuesAndDrainTheirShutdownTails()
    {
        await using var database = await JitAuditQueueDatabase.CreateAsync(fixture.ConnectionString);
        await using var first = JitAuditQueueHost.Create(database.ConnectionString, "first", holdBeforeElection: false);
        await using var second = JitAuditQueueHost.Create(database.ConnectionString, "second", holdBeforeElection: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await first.StartAsync(deadline.Token);
        await second.StartAsync(deadline.Token);
        first.AssertProductionComposition();
        second.AssertProductionComposition();

        var firstRequest = Guid.NewGuid();
        var secondRequest = Guid.NewGuid();
        await first.EnqueueThroughHttpAsync(firstRequest, deadline.Token);
        await second.EnqueueThroughHttpAsync(secondRequest, deadline.Token);

        // These are the default ten-second timers. No explicit flush or Stop has run,
        // and each host has accepted only one event per queue rather than a full batch.
        await Task.WhenAll(first.Writes.BillingCommitted.Task, first.Writes.RequestCommitted.Task,
                second.Writes.BillingCommitted.Task, second.Writes.RequestCommitted.Task)
            .WaitAsync(TimeSpan.FromSeconds(25), deadline.Token);
        await Task.WhenAll(first.Election.BothAuditFollowersObserved, second.Election.BothAuditFollowersObserved)
            .WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
        first.Election.AssertFollowerChecksOccurred();
        second.Election.AssertFollowerChecksOccurred();
        await database.AssertExactRowsAsync([firstRequest, secondRequest], deadline.Token);

        var firstTail = Guid.NewGuid();
        var secondTail = Guid.NewGuid();
        await first.EnqueueThroughHttpAsync(firstTail, deadline.Token);
        await second.EnqueueThroughHttpAsync(secondTail, deadline.Token);
        // Independently establish that these accepted tails are still pending before Stop.
        await database.AssertExactRowsAsync([firstRequest, secondRequest], deadline.Token);
        await Task.WhenAll(first.StopAsync(deadline.Token), second.StopAsync(deadline.Token));
        await database.AssertExactRowsAsync([firstRequest, secondRequest, firstTail, secondTail], deadline.Token);
        first.Election.AssertFollowerChecksOccurred();
        second.Election.AssertFollowerChecksOccurred();
    }

    [Fact]
    public async Task HttpQueuesDrainWhenShutdownBeginsBeforeTheFirstLeadershipCheck()
    {
        await using var database = await JitAuditQueueDatabase.CreateAsync(fixture.ConnectionString);
        await using var host = JitAuditQueueHost.Create(database.ConnectionString, "before-election", holdBeforeElection: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(deadline.Token);
        host.AssertProductionComposition();
        // An owned logger latch holds both retention wrappers before their initial
        // five-second delay, making the lifecycle boundary independent of machine speed.
        await host.BeforeFirstElectionEntered.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
        var request = Guid.NewGuid();
        await host.EnqueueThroughHttpAsync(request, deadline.Token);
        await database.AssertExactRowsAsync([], deadline.Token);
        host.Election.AssertNoAuditChecksOrAcquisitions();

        // Concurrent host stopping cancels every retention wrapper before releasing
        // the latch. Stop and disposal join the released workers even on test failure.
        await host.StopBeforeFirstElectionAsync(deadline.Token);
        host.Election.AssertNoAuditChecksOrAcquisitions();
        await database.AssertExactRowsAsync([request], deadline.Token);
    }
}
