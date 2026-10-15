using System.Runtime.ExceptionServices;

namespace ConduitLLM.IntegrationTests.Tests;

// Cleanup runs after failed or cancelled work, with its own deadline supplied by
// the resource owner. Retain the original work failure when cleanup also fails.
internal static class IntegrationProofCleanup
{
    internal static async Task RunAsync(Func<Task> work, Func<Task> cleanup, string failureMessage)
    {
        ExceptionDispatchInfo? primary = null;
        try { await work(); }
        // This fixture boundary must run cleanup after every kind of work failure,
        // then rethrow that same exception with its original stack and cancellation token.
        catch (Exception exception) { primary = ExceptionDispatchInfo.Capture(exception); }

        try { await cleanup(); }
        catch (Exception exception)
        {
            // Keep both failures in order; catching only transport exceptions here
            // would let an unexpected cleanup failure replace the primary failure.
            if (primary is not null)
                throw new AggregateException(failureMessage, primary.SourceException, exception);
            throw;
        }
        primary?.Throw();
    }

    internal static async Task DrainIndependentlyAsync(
        IEnumerable<Func<CancellationToken, Task>> drains, TimeSpan timeout, string failureMessage)
    {
        var failures = new List<Exception>();
        foreach (var drain in drains)
        {
            // Each owned worker gets a fresh budget even if an earlier drain expires.
            using var cleanup = new CancellationTokenSource(timeout);
            try { await drain(cleanup.Token); }
            // Every remaining owner must be drained even if this owner fails unexpectedly.
            // These failures are propagated together after all independent attempts.
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException(failureMessage, failures);
    }
}

public sealed class IntegrationProofCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedWorkAndDrainFailuresRetainTheirIdentityAndStillAttemptEveryOwner(bool synchronous)
    {
        var primary = new NotSupportedException("Injected unsupported fixture operation.");
        var first = new ArgumentException("Injected first owner failure.");
        var second = new FormatException("Injected second owner failure.");
        var lastDrained = false;
        Task Work() => synchronous ? throw primary : Task.FromException(primary);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => IntegrationProofCleanup.RunAsync(
            Work, () => IntegrationProofCleanup.DrainIndependentlyAsync(
                [_ => Task.FromException(first), _ => Task.FromException(second),
                 _ => { lastDrained = true; return Task.CompletedTask; }],
                TimeSpan.FromSeconds(5), "Independent owners failed."), "Work and cleanup failed."));

        Assert.True(lastDrained);
        Assert.Same(primary, failure.InnerExceptions[0]);
        var cleanup = Assert.IsType<AggregateException>(failure.InnerExceptions[1]);
        Assert.Collection(cleanup.InnerExceptions,
            exception => Assert.Same(first, exception), exception => Assert.Same(second, exception));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledWorkAndCleanupKeepBothOriginalExceptions(bool cancelled)
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Exception primary = cancelled ? new OperationCanceledException(caller.Token)
            : new InvalidOperationException("Injected primary fixture failure.");
        var secondary = new IOException("Injected fixture cleanup failure.");
        var cleanupCalled = false;

        var failure = await Assert.ThrowsAsync<AggregateException>(() => IntegrationProofCleanup.RunAsync(
            async () => { await Task.Yield(); throw primary; },
            async () => { await Task.Yield(); cleanupCalled = true; throw secondary; },
            "Work and owned cleanup failed."));

        Assert.True(cleanupCalled);
        Assert.Collection(failure.InnerExceptions,
            exception => Assert.Same(primary, exception), exception => Assert.Same(secondary, exception));
    }

    [Fact]
    public async Task CancelledWorkStillRunsCleanupWithAnIndependentLiveDeadline()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var released = false;
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IntegrationProofCleanup.RunAsync(
            () => Task.Delay(Timeout.Infinite, caller.Token),
            async () =>
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Task.Delay(1, cleanup.Token);
                released = true;
            }, "Work and owned cleanup failed."));

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.True(released);
    }

    [Fact]
    public async Task CleanupFailureAfterSuccessfulWorkPropagatesTheOriginalException()
    {
        var secondary = new IOException("Injected fixture cleanup failure.");
        var failure = await Assert.ThrowsAsync<IOException>(() => IntegrationProofCleanup.RunAsync(
            () => Task.CompletedTask, () => Task.FromException(secondary), "Owned cleanup failed."));
        Assert.Same(secondary, failure);
    }

    [Fact]
    public async Task ExpiredFirstDrainStillGivesTheSecondDrainAnIndependentLiveBudgetAndKeepsPrimaryFailure()
    {
        var primary = new InvalidOperationException("Injected billing timer failure.");
        var firstToken = CancellationToken.None;
        var secondDrained = false;
        var failure = await Assert.ThrowsAsync<AggregateException>(() => IntegrationProofCleanup.RunAsync(
            () => Task.FromException(primary),
            () => IntegrationProofCleanup.DrainIndependentlyAsync(
                [async token =>
                    {
                        firstToken = token;
                        await Task.Delay(Timeout.Infinite, token);
                    },
                 token =>
                    {
                        Assert.True(firstToken.IsCancellationRequested);
                        Assert.False(token.IsCancellationRequested);
                        Assert.NotEqual(firstToken, token);
                        secondDrained = true;
                        return Task.CompletedTask;
                    }],
                TimeSpan.FromMilliseconds(50), "Owned billing timer drains failed."),
            "Billing timer work and cleanup failed."));

        Assert.True(secondDrained);
        Assert.Same(primary, failure.InnerExceptions[0]);
        var cleanup = Assert.IsType<AggregateException>(failure.InnerExceptions[1]);
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(cleanup.InnerExceptions));
        Assert.Equal(firstToken, cancelled.CancellationToken);
    }
}
