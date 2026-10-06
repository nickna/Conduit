using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "DistributedLock")]
public sealed class DistributedLockCutoverTests(PostgresLockTestContainerFixture fixture)
{
    private PostgresDistributedLockProvider Provider() => new(fixture.ConnectionString,
        NullLogger<PostgresDistributedLockProvider>.Instance);

    [Fact]
    public async Task Cutover_QuiescesTriggers_AndDrainsLegacyWorkEvenAfterItsLeaseEnds()
    {
        const string key = "test:cutover:drain";
        await using var legacy = await LegacyAdvisoryLockFixture.TryAcquireAsync(fixture.ConnectionString, key);
        Assert.NotNull(legacy);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = finish.Task; // Represents already started, uncooperative legacy work.
        await legacy.DisposeAsync(); // The former timer could do this while work was still running.
        await using (var unsafeSuccessor = await Provider().TryAcquireAsync(key))
        {
            Assert.NotNull(unsafeSuccessor);
            Assert.False(work.IsCompleted); // Stable identities cannot fence an expired worker.
        }
        // The operator quiesces new triggers, then awaits work before allowing cutover.
        var resumed = 0;
        async Task CutoverAsync()
        {
            await work;
            await using var ownership = await Provider().TryAcquireAsync(key);
            Assert.NotNull(ownership);
            Interlocked.Increment(ref resumed);
        }
        var cutover = CutoverAsync();
        try { Assert.Equal(0, Volatile.Read(ref resumed)); Assert.False(cutover.IsCompleted); }
        finally { finish.TrySetResult(); await cutover.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, resumed);
    }

    [Fact]
    public async Task Rollback_DrainsCanceledModernWork_BeforeResumingLegacyAcquisition()
    {
        const string key = "test:rollback:drain";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var work = Provider().RunWithOptionalLockAsync(key, TimeSpan.Zero,
            async (acquired, _) => { Assert.True(acquired); started.TrySetResult(); await finish.Task; return true; },
            NullLogger.Instance, shutdown.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown.Cancel();
            Assert.False(work.IsCompleted);
            Assert.Null(await LegacyAdvisoryLockFixture.TryAcquireAsync(fixture.ConnectionString, key));
        }
        finally
        {
            finish.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        // Only after drain does the operator restart the old version and resume triggers.
        await using var rollback = await LegacyAdvisoryLockFixture.TryAcquireAsync(fixture.ConnectionString, key);
        Assert.NotNull(rollback);
    }
}
