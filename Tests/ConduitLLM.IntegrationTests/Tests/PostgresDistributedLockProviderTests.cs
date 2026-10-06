using ConduitLLM.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "DistributedLock")]
public sealed class PostgresDistributedLockProviderTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task OptionalHelper_ActualContention_SkipsOrFallsBackAccordingToCallerPolicy()
    {
        const string key = "test:optional:policy";
        await using var ownership = await CreateProvider().TryAcquireAsync(key);
        Assert.NotNull(ownership);
        var calls = 0;
        Task<int> Operation(bool acquired, CancellationToken _) { Assert.False(acquired); calls++; return Task.FromResult(42); }
        var skipped = await CreateProvider().RunWithOptionalLockAsync(key, TimeSpan.FromMilliseconds(50), Operation,
            NullLogger.Instance, skipOnTimeout: true);
        Assert.False(skipped.Executed);
        Assert.Equal(0, calls);
        var fallback = await CreateProvider().RunWithOptionalLockAsync(key, TimeSpan.FromMilliseconds(50), Operation,
            NullLogger.Instance);
        Assert.Equal(42, fallback.Value);
        Assert.Equal(1, calls);
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider().RunWithOptionalLockAsync(
            key, TimeSpan.FromSeconds(10), Operation, NullLogger.Instance, canceled.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OptionalHelper_ActualBackendOutage_RunsFallbackOnceEvenForDiscoveryPolicy()
    {
        var unavailable = new PostgresDistributedLockProvider(
            "Host=127.0.0.1;Port=1;Database=locktest;Username=locktest;Password=locktest",
            NullLogger<PostgresDistributedLockProvider>.Instance);
        var calls = 0;
        var result = await unavailable.RunWithOptionalLockAsync("test:optional:outage", TimeSpan.Zero,
            (acquired, _) => { Assert.False(acquired); calls++; return Task.FromResult(42); }, NullLogger.Instance, skipOnTimeout: true);
        Assert.True(result.Executed);
        Assert.Equal(1, calls);
    }

    private PostgresDistributedLockProvider CreateProvider() => new(fixture.ConnectionString,
        NullLogger<PostgresDistributedLockProvider>.Instance);
    private PostgresDistributedLockService CreateLegacy() => new(new ContextFactory(fixture.ConnectionString),
        NullLogger<PostgresDistributedLockService>.Instance);

    [Fact]
    public async Task IndependentProviders_ExcludeSameKey_AllowDistinctKeys_ReleaseAsynchronously()
    {
        var first = CreateProvider();
        var second = CreateProvider();
        var holder = await first.TryAcquireAsync("test:adapter:exclusion");
        Assert.NotNull(holder);
        await using (holder)
        {
            Assert.Null(await second.TryAcquireAsync("test:adapter:exclusion"));
            await using var distinct = await second.TryAcquireAsync("test:adapter:distinct");
            Assert.NotNull(distinct);
        }
        await holder.DisposeAsync();
        await using var successor = await second.TryAcquireAsync("test:adapter:exclusion");
        Assert.NotNull(successor);
    }

    [Fact]
    public async Task LegacyThenNew_AndNewThenLegacy_InteroperateInSingleBigintNamespace()
    {
        const string key = "test:adapter:interop";
        var legacy = CreateLegacy();
        var provider = CreateProvider();
        await using (var old = await legacy.AcquireLockAsync(key, TimeSpan.FromMinutes(1)))
        {
            Assert.NotNull(old);
            Assert.Null(await provider.TryAcquireAsync(key));
        }
        await using (var modern = await provider.TryAcquireAsync(key))
        {
            Assert.NotNull(modern);
            Assert.Null(await legacy.AcquireLockAsync(key, TimeSpan.FromMinutes(1)));
        }
        await using var successor = await provider.TryAcquireAsync(key);
        Assert.NotNull(successor);
    }

    [Fact]
    public async Task HealthyHolder_RemainsHeldBeyondScaledFormerLease_AndDbContextChurn()
    {
        const string key = "test:adapter:lifetime";
        await using var holder = await CreateProvider().TryAcquireAsync(key);
        Assert.NotNull(holder);
        for (var i = 0; i < 5; i++)
        {
            await using var context = new ContextFactory(fixture.ConnectionString).CreateDbContext();
            await context.Database.OpenConnectionAsync();
            await context.Database.CloseConnectionAsync();
        }
        await Task.Delay(350); // Former expiry scaled to 100ms.
        Assert.False(holder.HandleLostToken.IsCancellationRequested);
        Assert.Null(await CreateProvider().TryAcquireAsync(key));
    }

    [Fact]
    public async Task WaitingAcquisition_TimesOutAsBusy_AndCancellationPropagates()
    {
        const string key = "test:adapter:wait";
        await using var holder = await CreateProvider().TryAcquireAsync(key);
        Assert.NotNull(holder);
        Assert.Null(await CreateProvider().TryAcquireAsync(key, TimeSpan.FromMilliseconds(100)));
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider().TryAcquireAsync(
            key, TimeSpan.FromSeconds(10), canceled.Token));
    }

    [Fact]
    public async Task OperationException_DisposesBeforeSuccessorAcquires()
    {
        const string key = "test:adapter:exception";
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var ownership = await CreateProvider().TryAcquireAsync(key);
            Assert.NotNull(ownership);
            throw new InvalidOperationException("Protected operation failed");
        });
        await using var successor = await CreateProvider().TryAcquireAsync(key);
        Assert.NotNull(successor);
    }

    [Fact]
    public async Task UnavailableBackend_ThrowsRatherThanReportingContention()
    {
        var unavailable = new PostgresDistributedLockProvider(
            "Host=127.0.0.1;Port=1;Database=locktest;Username=locktest;Password=locktest",
            NullLogger<PostgresDistributedLockProvider>.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => unavailable.TryAcquireAsync("test:adapter:outage"));
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(connectionString).Options);
    }
}
