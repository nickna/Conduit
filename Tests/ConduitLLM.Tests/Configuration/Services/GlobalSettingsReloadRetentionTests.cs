#nullable enable

using System.Collections;
using System.Reflection;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Services;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed partial class GlobalSettingsReloadRetryTests
{
    private const int CompletedReloadLimit = 10_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedReloadCapacity_EvictsOldestCompletionAndAllowsItsLateReplay(bool subscribed)
    {
        await using var fixture = new Fixture(subscribed);
        await fixture.Service.StartAsync(CancellationToken.None);
        for (var index = 0; index < CompletedReloadLimit + 3; index++)
            await RequestReloadAsync(fixture, subscribed, $"capacity-{index}").WaitAsync(TestTimeout);

        var retained = AssertRetainedReloadCount(fixture.Service, CompletedReloadLimit);
        Assert.Equal("capacity-3", retained.CompletionOrder[0]);
        Assert.Equal($"capacity-{CompletedReloadLimit + 2}", retained.CompletionOrder[^1]);
        Assert.DoesNotContain("capacity-0", retained.Completed);
        Assert.Empty(retained.InFlight);
        var calls = fixture.ListCalls;
        Assert.Equal(CompletedReloadLimit + 4, calls);

        // Accessing a completed ID does not promote it in the FIFO queue.
        await RequestReloadAsync(fixture, subscribed, "capacity-3").WaitAsync(TestTimeout);
        await RequestReloadAsync(fixture, subscribed, $"capacity-{CompletedReloadLimit + 2}").WaitAsync(TestTimeout);
        Assert.Equal(calls, fixture.ListCalls);
        fixture.Load = (_, _) => Task.FromResult(Settings(41));
        await RequestReloadAsync(fixture, subscribed, "capacity-0").WaitAsync(TestTimeout);

        Assert.Equal(calls + 1, fixture.ListCalls);
        Assert.Equal(41, await fixture.Service.GetMaxAgenticIterationsAsync());
        retained = AssertRetainedReloadCount(fixture.Service, CompletedReloadLimit);
        Assert.Equal("capacity-4", retained.CompletionOrder[0]);
        Assert.Equal("capacity-0", retained.CompletionOrder[^1]);
        Assert.DoesNotContain("capacity-3", retained.Completed);
        Assert.Contains($"capacity-{CompletedReloadLimit + 2}", retained.Completed);

        await RequestReloadAsync(fixture, subscribed, "capacity-0").WaitAsync(TestTimeout);
        await RequestReloadAsync(fixture, subscribed, $"capacity-{CompletedReloadLimit + 2}").WaitAsync(TestTimeout);
        Assert.Equal(calls + 1, fixture.ListCalls);
        AssertRetainedReloadCount(fixture.Service, CompletedReloadLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedReloadExpiry_ExpiresAtTwentyFourHoursWithoutRefreshingOnDuplicateAccess(bool subscribed)
    {
        await using var fixture = new Fixture(subscribed);
        await fixture.Service.StartAsync(CancellationToken.None);
        await RequestReloadAsync(fixture, subscribed, "older").WaitAsync(TestTimeout);
        fixture.Clock.Advance(TimeSpan.FromHours(12));
        await RequestReloadAsync(fixture, subscribed, "newer").WaitAsync(TestTimeout);
        fixture.Clock.Advance(TimeSpan.FromHours(12) - TimeSpan.FromTicks(1));

        await RequestReloadAsync(fixture, subscribed, "older").WaitAsync(TestTimeout);

        Assert.Equal(3, fixture.ListCalls);
        AssertRetainedReloadCount(fixture.Service, 2);
        fixture.Load = (_, _) => Task.FromResult(Settings(43));
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        await RequestReloadAsync(fixture, subscribed, "older").WaitAsync(TestTimeout);

        Assert.Equal(4, fixture.ListCalls);
        Assert.Equal(43, await fixture.Service.GetMaxAgenticIterationsAsync());
        var retained = AssertRetainedReloadCount(fixture.Service, 2);
        Assert.Equal(new[] { "newer", "older" }, retained.CompletionOrder);
        await RequestReloadAsync(fixture, subscribed, "newer").WaitAsync(TestTimeout);
        await RequestReloadAsync(fixture, subscribed, "older").WaitAsync(TestTimeout);
        Assert.Equal(4, fixture.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedReloadExpiry_UsesElapsedTimeDespiteForwardAndBackwardWallClockChanges(bool subscribed)
    {
        await using var fixture = new Fixture(subscribed);
        await fixture.Service.StartAsync(CancellationToken.None);
        await RequestReloadAsync(fixture, subscribed, "wall-clock").WaitAsync(TestTimeout);

        fixture.Clock.WallClockOffset = TimeSpan.FromHours(48);
        await RequestReloadAsync(fixture, subscribed, "wall-clock").WaitAsync(TestTimeout);

        Assert.Equal(2, fixture.ListCalls);
        AssertRetainedReloadCount(fixture.Service, 1);
        fixture.Clock.WallClockOffset = TimeSpan.FromHours(-48);
        fixture.Clock.Advance(TimeSpan.FromHours(24));
        fixture.Load = (_, _) => Task.FromResult(Settings(47));
        await RequestReloadAsync(fixture, subscribed, "wall-clock").WaitAsync(TestTimeout);

        Assert.Equal(3, fixture.ListCalls);
        Assert.Equal(47, await fixture.Service.GetMaxAgenticIterationsAsync());
        AssertRetainedReloadCount(fixture.Service, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InFlightReload_SurvivesElapsedRetentionWindowAndPruningUntilItsActualCompletion(
        bool subscribed, bool pruneOnRequest)
    {
        await using var fixture = new Fixture(subscribed);
        await fixture.Service.StartAsync(CancellationToken.None);
        await RequestReloadAsync(fixture, subscribed, "expired-completion").WaitAsync(TestTimeout);
        var entered = Signal();
        var response = new TaskCompletionSource<IReadOnlyList<GlobalSetting>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Load = (attempt, _) =>
        {
            if (attempt != 3)
                return Task.FromResult(Settings(53));
            entered.TrySetResult();
            return response.Task;
        };
        var first = RequestReloadAsync(fixture, subscribed, "still-running");
        var duplicate = RequestReloadAsync(fixture, subscribed, "still-running");
        Task pruning = Task.CompletedTask;
        Task lateDuplicate = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            fixture.Clock.Advance(TimeSpan.FromHours(48));
            if (pruneOnRequest)
            {
                // This request prunes completed IDs before waiting behind the
                // repository lock. The active request remains a shared task.
                pruning = RequestReloadAsync(fixture, subscribed, "pruning-request");
                lateDuplicate = RequestReloadAsync(fixture, subscribed, "still-running");
                var retained = AssertRetainedReloadCount(fixture.Service, 0);
                Assert.Equal(new[] { "pruning-request", "still-running" }, retained.InFlight.Order().ToArray());
                Assert.False(pruning.IsCompleted);
                Assert.False(lateDuplicate.IsCompleted);
            }
            else
            {
                // Pruning is lazy. Without another delivery, completion itself
                // must evict the old ID after the long-running load finishes.
                var retained = AssertRetainedReloadCount(fixture.Service, 1);
                Assert.Contains("expired-completion", retained.Completed);
                Assert.Equal(new[] { "still-running" }, retained.InFlight);
            }

            Assert.False(first.IsCompleted);
            Assert.False(duplicate.IsCompleted);
            Assert.Equal(3, fixture.ListCalls);
        }
        finally
        {
            response.TrySetResult(Settings(51));
        }

        await Task.WhenAll(first, duplicate, pruning, lateDuplicate).WaitAsync(TestTimeout);

        var calls = pruneOnRequest ? 4 : 3;
        Assert.Equal(calls, fixture.ListCalls);
        var completed = AssertRetainedReloadCount(fixture.Service, pruneOnRequest ? 2 : 1);
        Assert.DoesNotContain("expired-completion", completed.Completed);
        Assert.Contains("still-running", completed.Completed);
        Assert.Empty(completed.InFlight);
        // Retention begins at successful completion, not at reservation time.
        await RequestReloadAsync(fixture, subscribed, "still-running").WaitAsync(TestTimeout);
        Assert.Equal(calls, fixture.ListCalls);
    }

    private static Task RequestReloadAsync(Fixture fixture, bool subscribed, string requestId)
    {
        if (!subscribed)
            return fixture.Service.PublishReloadAsync(requestId);

        fixture.Deliver(requestId);
        return ProcessReloadRequestAsync(fixture.Service, requestId);
    }

    private static ReloadRetentionState AssertRetainedReloadCount(GlobalSettingsCacheService service, int expected)
    {
        object Field(string name) => typeof(GlobalSettingsCacheService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        lock (Field("_reloadGate"))
        {
            var completed = new HashSet<string>((HashSet<string>)Field("_processedReloads"), StringComparer.Ordinal);
            var order = ((IEnumerable)Field("_processedReloadOrder")).Cast<object>()
                .Select(entry => (string)entry.GetType().GetProperty("RequestId")!.GetValue(entry)!).ToArray();
            var inFlight = ((Dictionary<string, TaskCompletionSource>)Field("_reloadsInFlight")).Keys.ToArray();
            Assert.InRange(completed.Count, 0, CompletedReloadLimit);
            Assert.Equal(expected, completed.Count);
            Assert.Equal(expected, order.Length);
            Assert.Equal(expected, order.Distinct(StringComparer.Ordinal).Count());
            Assert.All(order, requestId => Assert.Contains(requestId, completed));
            return new ReloadRetentionState(completed, order, inFlight);
        }
    }

    private sealed record ReloadRetentionState(HashSet<string> Completed, string[] CompletionOrder, string[] InFlight);
}
