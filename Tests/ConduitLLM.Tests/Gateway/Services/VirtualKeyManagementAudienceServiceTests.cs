using System.Collections.Concurrent;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class VirtualKeyManagementAudienceServiceTests
{
    [Theory]
    [InlineData("demoted")]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("malformed")]
    [InlineData("lookup-failure")]
    public async Task AuditRevokesUntrustedRolesPreservesOwnersAndRequiresFreshPromotion(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        await fixture.RegisterAsync("owner", 3, administrator: false);
        fixture.Keys[2] = Key(2, administrator: false);
        Assert.True((await fixture.Service.SubscribeAsync("admin", 2, static (_, _) => Task.CompletedTask,
            CancellationToken.None)).Authorized);
        Assert.True(fixture.Has("admin", "admin"));
        Assert.True(fixture.Has("admin", "vkey-status-2"));

        switch (failure)
        {
            case "demoted": fixture.Keys[1].Metadata = "{\"isAdmin\":false}"; break;
            case "missing": fixture.Keys.TryRemove(1, out _); break;
            case "disabled": fixture.Keys[1].IsEnabled = false; break;
            case "malformed": fixture.Keys[1].Metadata = "{"; break;
            case "lookup-failure": fixture.Lookup = (id, _) => id == 1
                ? Task.FromException<VirtualKey?>(new IOException("Owned lookup failure"))
                : Task.FromResult(fixture.Keys.GetValueOrDefault(id)); break;
        }
        await fixture.Service.AuditAsync();

        Assert.False(fixture.Has("admin", "admin"));
        Assert.False(fixture.Has("admin", "vkey-status-2"));
        Assert.True(fixture.Has("admin", "vkey-status-1"));
        Assert.True(fixture.Has("admin", "vkey-lifecycle-owner-1"));
        Assert.True(fixture.Has("owner", "vkey-status-3"));
        Assert.True(fixture.Has("owner", "vkey-lifecycle-owner-3"));
        Assert.Empty(fixture.Aborted);

        fixture.Lookup = null;
        fixture.Keys[1] = Key(1);
        await fixture.Service.AuditAsync();
        Assert.False(fixture.Has("admin", "admin")); // An audit never grants a new role.
        Assert.True((await fixture.Service.SubscribeAsync("admin", 2, static (_, _) => Task.CompletedTask,
            CancellationToken.None)).Authorized);
        Assert.True(fixture.Has("admin", "admin"));
        Assert.True(fixture.Has("admin", "vkey-status-2"));
        Assert.False(fixture.Has("admin", "vkey-lifecycle-owner-1"));
    }

    [Fact]
    public async Task SubscribeAsync_TargetLookupFails_PreservesAudiencesAndAllowsRetryAndRevocationAsync()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        fixture.Keys[2] = Key(2, administrator: false);
        fixture.Keys[3] = Key(3, administrator: false);
        await fixture.Service.SubscribeAsync("admin", 2, static (_, _) => Task.CompletedTask, CancellationToken.None);
        var before = fixture.Memberships.Keys.ToHashSet();
        var failure = new IOException("Owned target lookup failure");
        var sent = 0;
        fixture.Lookup = (id, _) => id == 3 ? Task.FromException<VirtualKey?>(failure)
            : Task.FromResult(fixture.Keys.GetValueOrDefault(id));

        var observed = await Assert.ThrowsAsync<IOException>(() => fixture.Service.SubscribeAsync("admin", 3,
            (_, _) => { sent++; return Task.CompletedTask; }, CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.Equal(0, sent);
        Assert.Empty(fixture.Aborted);
        Assert.Equal(1, fixture.Service.ConnectionCount);
        Assert.True(before.SetEquals(fixture.Memberships.Keys));

        fixture.Lookup = null;
        var retried = await fixture.Service.SubscribeAsync("admin", 3,
            (_, _) => { sent++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(retried.Authorized && retried.TargetExists);
        Assert.Equal(1, sent);
        Assert.True(fixture.Has("admin", "vkey-status-2"));
        Assert.True(fixture.Has("admin", "vkey-status-3"));
        fixture.Keys[1].Metadata = "{\"isAdmin\":false}";
        await fixture.Service.AuditAsync();
        Assert.False(fixture.Has("admin", "admin"));
        Assert.False(fixture.Has("admin", "vkey-status-2"));
        Assert.False(fixture.Has("admin", "vkey-status-3"));
        Assert.True(fixture.Has("admin", "vkey-status-1"));
        Assert.True(fixture.Has("admin", "vkey-lifecycle-owner-1"));
        Assert.Empty(fixture.Aborted);
    }

    [Fact]
    public async Task SubscribeAsync_PromotedAdministratorTargetLookupFails_DoesNotGrantAudienceAsync()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("promoted", 1, administrator: false);
        fixture.Keys[1] = Key(1);
        var before = fixture.Memberships.Keys.ToHashSet();
        var failure = new IOException("Owned target lookup failure");
        fixture.Lookup = (id, _) => id == 2 ? Task.FromException<VirtualKey?>(failure)
            : Task.FromResult(fixture.Keys.GetValueOrDefault(id));
        var sent = 0;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.Service.SubscribeAsync("promoted", 2,
            (_, _) => { sent++; return Task.CompletedTask; }, CancellationToken.None)));

        Assert.Equal(0, sent);
        Assert.Empty(fixture.Aborted);
        Assert.Equal(1, fixture.Service.ConnectionCount);
        Assert.True(before.SetEquals(fixture.Memberships.Keys));
        Assert.False(fixture.Has("promoted", "admin"));
        Assert.True(fixture.Has("promoted", "vkey-lifecycle-owner-1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscribeAsync_TargetLookupCancelled_AbortsAndSuppressesLateSubscriptionAsync(bool callerCancellation)
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        fixture.Keys[2] = Key(2, administrator: false);
        using var caller = new CancellationTokenSource();
        var started = Signal();
        var target = new TaskCompletionSource<VirtualKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = Signal();
        fixture.Lookup = (id, _) =>
        {
            if (id != 2) return Task.FromResult(fixture.Keys.GetValueOrDefault(id));
            started.TrySetResult();
            return target.Task; // Deliberately ignores cancellation until the owned read drains.
        };
        fixture.ScopeDisposed = _ => drained.TrySetResult();
        var sent = 0;
        try
        {
            var subscribe = fixture.Service.SubscribeAsync("admin", 2,
                (_, _) => { Interlocked.Increment(ref sent); return Task.CompletedTask; }, caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callerCancellation) caller.Cancel();
            else fixture.Clock.Advance(VirtualKeyManagementAudienceService.OperationBudget);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscribe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, fixture.Aborted.GetValueOrDefault("admin"));
            Assert.Equal(0, fixture.Service.ConnectionCount);
            Assert.False(drained.Task.IsCompleted);
            Assert.Empty(fixture.Memberships);
            Assert.Equal(0, sent);
        }
        finally
        {
            target.TrySetResult(fixture.Keys[2]);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, sent);
        Assert.Empty(fixture.Memberships);
        await Assert.ThrowsAsync<HubException>(() => fixture.Service.SubscribeAsync("admin", 2,
            static (_, _) => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task SubscribeAsync_TargetLookupThrowsCancellationWithoutCallerCancellation_AbortsAsync()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        var failure = new OperationCanceledException("Owned target lookup cancellation");
        fixture.Lookup = (id, _) => id == 2 ? Task.FromException<VirtualKey?>(failure)
            : Task.FromResult(fixture.Keys.GetValueOrDefault(id));

        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.SubscribeAsync("admin", 2,
            static (_, _) => Task.CompletedTask, CancellationToken.None)));

        Assert.Equal(1, fixture.Aborted.GetValueOrDefault("admin"));
        Assert.Equal(0, fixture.Service.ConnectionCount);
        Assert.Empty(fixture.Memberships);
    }

    [Fact]
    public async Task HostedAuditUsesItsPeriodicClockAndStopsWithItsHost()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        fixture.Keys[2] = Key(2, administrator: false);
        await fixture.Service.SubscribeAsync("admin", 2, static (_, _) => Task.CompletedTask, CancellationToken.None);
        var restored = Signal();
        fixture.Added = (id, group) =>
        {
            if (id == "admin" && group == "vkey-lifecycle-owner-1") restored.TrySetResult();
        };
        try
        {
            await fixture.Service.StartAsync(CancellationToken.None);
            await fixture.Clock.PeriodicTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Keys[1].Metadata = "{\"isAdmin\":false}";
            fixture.Clock.Advance(VirtualKeyManagementAudienceService.AuditInterval);
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Has("admin", "admin"));
            Assert.False(fixture.Has("admin", "vkey-status-2"));
            Assert.True(fixture.Has("admin", "vkey-status-1"));
        }
        finally { await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, fixture.Service.ConnectionCount);
    }

    [Fact]
    public async Task SharedDeadlineBoundsNoncooperativeLookupAndKeepsHealthyAndAlreadyRevokedOwners()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("healthy", 1);
        await fixture.RegisterAsync("stalled", 2);
        await fixture.RegisterAsync("demoted", 3);
        fixture.Keys[3].Metadata = "{\"isAdmin\":false}";
        var pending = new TaskCompletionSource<VirtualKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal();
        var healthyFinished = Signal();
        var demotionFinished = Signal();
        var lateScopeDisposed = Signal();
        fixture.ScopeDisposed = id =>
        {
            if (id == 1) healthyFinished.TrySetResult();
            if (id == 3) demotionFinished.TrySetResult();
            if (id == 2) lateScopeDisposed.TrySetResult();
        };
        fixture.Lookup = (id, _) =>
        {
            if (id != 2) return Task.FromResult(fixture.Keys.GetValueOrDefault(id));
            started.TrySetResult();
            return pending.Task; // Deliberately ignores the token.
        };
        try
        {
            var audit = fixture.Service.AuditAsync();
            await Task.WhenAll(started.Task, healthyFinished.Task, demotionFinished.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Has("demoted", "admin"));
            Assert.True(fixture.Has("demoted", "vkey-lifecycle-owner-3"));
            Assert.False(lateScopeDisposed.Task.IsCompleted);

            fixture.Clock.Advance(VirtualKeyManagementAudienceService.OperationBudget);
            await audit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Aborted.GetValueOrDefault("stalled"));
            Assert.Equal(0, fixture.Aborted.GetValueOrDefault("healthy"));
            Assert.Equal(0, fixture.Aborted.GetValueOrDefault("demoted"));
            Assert.True(fixture.Has("healthy", "admin"));
            Assert.True(fixture.Has("demoted", "vkey-status-3"));
            Assert.Equal(2, fixture.Service.ConnectionCount);
            Assert.False(lateScopeDisposed.Task.IsCompleted); // The owned scope survives the bounded wait.
        }
        finally
        {
            pending.TrySetResult(Key(2));
            await lateScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SharedDeadlineAlsoClosesOwnersQueuedBehindTheLookupConcurrencyLimit()
    {
        await using var fixture = new Fixture();
        for (var id = 1; id <= 20; id++) await fixture.RegisterAsync($"admin-{id}", id);
        var pending = new TaskCompletionSource<VirtualKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal();
        var drained = Signal();
        var lookups = 0;
        var disposed = 0;
        fixture.ScopeDisposed = _ =>
        {
            if (Interlocked.Increment(ref disposed) == 16) drained.TrySetResult();
        };
        fixture.Lookup = (_, _) =>
        {
            if (Interlocked.Increment(ref lookups) == 16) started.TrySetResult();
            return pending.Task;
        };
        try
        {
            var audit = fixture.Service.AuditAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(VirtualKeyManagementAudienceService.OperationBudget);
            await audit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(20, fixture.Aborted.Count);
            Assert.All(fixture.Aborted.Values, count => Assert.Equal(1, count));
            Assert.Equal(0, fixture.Service.ConnectionCount);
            Assert.Equal(16, lookups); // Four queued owners never acquired a lookup slot.
            Assert.Equal(0, disposed);
        }
        finally
        {
            pending.TrySetResult(null);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AuditCancellationAbortsUnresolvedOwnersBeforeRemainingCancellationCallbacksFinish()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("stalled", 1);
        using var caller = new CancellationTokenSource();
        var pending = new TaskCompletionSource<VirtualKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupStarted = Signal();
        var cancellationBlocked = Signal();
        var releaseCancellation = Signal();
        var aborted = Signal();
        var scopeDisposed = Signal();
        fixture.AbortedConnection = _ => aborted.TrySetResult();
        fixture.ScopeDisposed = _ => scopeDisposed.TrySetResult();
        fixture.Lookup = async (_, token) =>
        {
            using var cancellationBarrier = token.Register(() =>
            {
                cancellationBlocked.TrySetResult();
                releaseCancellation.Task.GetAwaiter().GetResult();
            });
            lookupStarted.TrySetResult();
            return await pending.Task;
        };

        var audit = fixture.Service.AuditAsync(caller.Token);
        Task cancellation = Task.CompletedTask;
        try
        {
            await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // WaitAsync registered after the audit revocation callback. The
            // lookup-token barrier holds cancellation dispatch between them.
            cancellation = Task.Run(() => caller.Cancel());
            await cancellationBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cancellation.IsCompleted);
            Assert.Equal(1, fixture.Aborted.GetValueOrDefault("stalled"));
            Assert.Equal(0, fixture.Service.ConnectionCount);
            Assert.Empty(fixture.Memberships);
            Assert.False(scopeDisposed.Task.IsCompleted);

            releaseCancellation.TrySetResult();
            await Task.WhenAll(audit, cancellation).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Aborted.GetValueOrDefault("stalled"));
        }
        finally
        {
            releaseCancellation.TrySetResult();
            pending.TrySetResult(Key(1));
            await Task.WhenAll(audit, cancellation, scopeDisposed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task GroupRemovalFailureAbortsInsteadOfRetainingPrivilegedMembership()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        fixture.Keys[2] = Key(2, administrator: false);
        await fixture.Service.SubscribeAsync("admin", 2, static (_, _) => Task.CompletedTask, CancellationToken.None);
        fixture.Keys[1].Metadata = "{\"isAdmin\":false}";
        fixture.Removing = (_, group, _) => group == "admin"
            ? Task.FromException(new IOException("Owned group removal failure")) : Task.CompletedTask;

        await fixture.Service.AuditAsync();

        Assert.Equal(1, fixture.Aborted.GetValueOrDefault("admin"));
        Assert.Equal(0, fixture.Service.ConnectionCount);
        Assert.Empty(fixture.Memberships);
    }

    [Fact]
    public async Task TimedOutAppliedJoinCannotSendPrivilegedStatusOrRejoinAfterItsLateCompletion()
    {
        await using var fixture = new Fixture();
        await fixture.RegisterAsync("admin", 1);
        fixture.Keys[2] = Key(2, administrator: false);
        var joined = Signal();
        var release = Signal();
        var drained = Signal();
        var sent = 0;
        fixture.Adding = (id, group, _) =>
        {
            if (id != "admin" || group != "vkey-status-2") return Task.CompletedTask;
            joined.TrySetResult();
            return release.Task; // The remote add has applied, but its acknowledgement is stalled.
        };
        fixture.ScopeDisposed = _ => drained.TrySetResult();
        try
        {
            var subscribe = fixture.Service.SubscribeAsync("admin", 2, (_, _) =>
            {
                Interlocked.Increment(ref sent);
                return Task.CompletedTask;
            }, CancellationToken.None);
            await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Has("admin", "vkey-status-2"));
            fixture.Clock.Advance(VirtualKeyManagementAudienceService.OperationBudget);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscribe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, fixture.Aborted.GetValueOrDefault("admin"));
            Assert.Equal(0, fixture.Service.ConnectionCount);
            Assert.False(drained.Task.IsCompleted);
            Assert.Equal(0, sent);
        }
        finally
        {
            release.TrySetResult();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, sent);
        Assert.Empty(fixture.Memberships);
        await Assert.ThrowsAsync<HubException>(() => fixture.Service.SubscribeAsync("admin", 2,
            static (_, _) => Task.CompletedTask, CancellationToken.None));
    }

    private static VirtualKey Key(int id, bool administrator = true) => new()
    {
        Id = id, IsEnabled = true, KeyName = $"audience-{id}",
        Metadata = administrator ? "{\"isAdmin\":true}" : null
    };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock : FakeTimeProvider
    {
        internal TaskCompletionSource PeriodicTimerCreated { get; } = Signal();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (period == VirtualKeyManagementAudienceService.AuditInterval) PeriodicTimerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly List<string> _connections = [];
        internal Clock Clock { get; } = new();
        internal ConcurrentDictionary<int, VirtualKey> Keys { get; } = new();
        internal ConcurrentDictionary<(string Connection, string Group), byte> Memberships { get; } = new();
        internal ConcurrentDictionary<string, int> Aborted { get; } = new();
        internal Func<int, CancellationToken, Task<VirtualKey?>>? Lookup;
        internal Action<int>? ScopeDisposed;
        internal Action<string>? AbortedConnection;
        internal Action<string, string>? Added;
        internal Func<string, string, CancellationToken, Task>? Adding;
        internal Func<string, string, CancellationToken, Task>? Removing;
        internal VirtualKeyManagementAudienceService Service { get; }

        internal Fixture()
        {
            var groups = new Mock<IGroupManager>();
            groups.Setup(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, string group, CancellationToken token) =>
                {
                    Memberships[(id, group)] = 0;
                    Added?.Invoke(id, group);
                    return Adding?.Invoke(id, group, token) ?? Task.CompletedTask;
                });
            groups.Setup(value => value.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string id, string group, CancellationToken token) =>
                {
                    if (Removing is { } remove) await remove(id, group, token);
                    Memberships.TryRemove((id, group), out _);
                });
            var hub = new Mock<IHubContext<VirtualKeyManagementHub>>();
            hub.SetupGet(value => value.Groups).Returns(groups.Object);
            _provider = new ServiceCollection().AddScoped<IVirtualKeyRuntimeService>(_ =>
            {
                var lastOwner = 0;
                var runtime = new Mock<IVirtualKeyRuntimeService>();
                runtime.Setup(value => value.GetVirtualKeyInfoForValidationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .Returns((int id, CancellationToken token) =>
                    {
                        lastOwner = id;
                        return Lookup?.Invoke(id, token) ?? Task.FromResult(Keys.GetValueOrDefault(id));
                    });
                runtime.As<IAsyncDisposable>().Setup(value => value.DisposeAsync()).Returns(() =>
                {
                    ScopeDisposed?.Invoke(lastOwner);
                    return ValueTask.CompletedTask;
                });
                return runtime.Object;
            }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Service = new VirtualKeyManagementAudienceService(hub.Object,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<VirtualKeyManagementAudienceService>.Instance, Clock);
        }

        internal async Task RegisterAsync(string connection, int owner, bool administrator = true)
        {
            Keys[owner] = Key(owner, administrator);
            _connections.Add(connection);
            await Service.RegisterAsync(connection, owner, () =>
            {
                Aborted.AddOrUpdate(connection, 1, static (_, count) => count + 1);
                foreach (var membership in Memberships.Keys.Where(value => value.Connection == connection))
                    Memberships.TryRemove(membership, out _);
                AbortedConnection?.Invoke(connection);
            }, CancellationToken.None);
        }

        internal bool Has(string connection, string group) => Memberships.ContainsKey((connection, group));

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
                await Service.RemoveAsync(connection).WaitAsync(TimeSpan.FromSeconds(5));
            Service.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
