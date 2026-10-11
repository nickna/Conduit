using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class NpgsqlConnectionPoolWarmupTests(PostgresLockTestContainerFixture fixture)
{
    private NpgsqlDataSource CreateSingleConnectionPool() => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            MinPoolSize = 0,
            MaxPoolSize = 1,
            Timeout = 5,
            CommandTimeout = 5
        }.ConnectionString);

    [Fact]
    public async Task ParallelWarmupsReleaseEveryLeaseAndLeaveDataSourceUsable()
    {
        await using var dataSource = CreateSingleConnectionPool();
        var warmup = new NpgsqlConnectionPoolWarmup(dataSource);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => warmup.WarmConnectionAsync(deadline.Token)));
        await AssertPoolUsableAsync(dataSource, deadline.Token);
    }

    [Fact]
    public async Task CancellationWhilePoolIsOccupiedRemovesWaitingLeaseAndAllowsRecovery()
    {
        await using var dataSource = CreateSingleConnectionPool();
        var warmup = new NpgsqlConnectionPoolWarmup(dataSource);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var holder = await dataSource.OpenConnectionAsync(deadline.Token))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var waiting = warmup.WarmConnectionAsync(cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        await warmup.WarmConnectionAsync(deadline.Token);
        await AssertPoolUsableAsync(dataSource, deadline.Token);
    }

    [Fact]
    public async Task AlreadyCancelledOperationDoesNotDisposeHostPool()
    {
        await using var dataSource = CreateSingleConnectionPool();
        var warmup = new NpgsqlConnectionPoolWarmup(dataSource);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warmup.WarmConnectionAsync(cancellation.Token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await warmup.WarmConnectionAsync(deadline.Token);
        await AssertPoolUsableAsync(dataSource, deadline.Token);
    }

    [Fact]
    public async Task ConnectionFailurePropagatesToCoordinator()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unreachable;Username=conduit;Password=conduit;Timeout=1");
        var warmup = new NpgsqlConnectionPoolWarmup(dataSource);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => warmup.WarmConnectionAsync(deadline.Token));
    }

    private static async Task AssertPoolUsableAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync(cancellationToken));
    }
}
