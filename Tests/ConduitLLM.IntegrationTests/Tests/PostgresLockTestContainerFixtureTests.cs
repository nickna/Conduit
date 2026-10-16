using ConduitLLM.IntegrationTests.Infrastructure;

using Npgsql;

using StackExchange.Redis;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "DistributedLock")]
[Trait("Component", "PostgresFixture")]
public sealed class PostgresLockTestContainerFixtureTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LifecycleConstructsStartsAndDisposesOnlyOwnedDependencies(bool externalPostgres, bool externalRedis)
    {
        var postgres = new RecordingContainer("owned-postgres");
        var redis = new RecordingContainer("owned-redis");
        var postgresImages = new List<string>();
        var redisImages = new List<string>();
        var fixture = new PostgresLockTestContainerFixture(
            externalPostgres ? "external-postgres" : null,
            externalRedis ? "external-redis" : null,
            image => { postgresImages.Add(image); return postgres; },
            image => { redisImages.Add(image); return redis; });

        Assert.Equal(externalPostgres ? Array.Empty<string>() : ["postgres:17-alpine"], postgresImages);
        Assert.Equal(externalRedis ? Array.Empty<string>() : ["redis:7.4-alpine"], redisImages);
        Assert.Equal(externalPostgres ? "external-postgres" : "owned-postgres", fixture.ConnectionString);
        Assert.Equal(externalRedis ? "external-redis" : "owned-redis", fixture.RedisConnectionString);
        Assert.Equal(0, postgres.Starts);
        Assert.Equal(0, redis.Starts);

        await fixture.InitializeAsync();
        Assert.Equal(externalPostgres ? 0 : 1, postgres.Starts);
        Assert.Equal(externalRedis ? 0 : 1, redis.Starts);

        await fixture.DisposeAsync();
        Assert.Equal(externalPostgres ? 0 : 1, postgres.Disposals);
        Assert.Equal(externalRedis ? 0 : 1, redis.Disposals);
        Assert.Equal(externalPostgres ? 0 : 1, postgres.ConnectionStringReads);
        Assert.Equal(externalRedis ? 0 : 1, redis.ConnectionStringReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothExternalServicesNeverProbeDeniedOrUnavailableDocker(bool denied)
    {
        PostgresLockTestContainerFixture.IOwnedContainer DockerUnavailable(string image)
        {
            if (denied) throw new UnauthorizedAccessException($"Docker access denied while constructing {image}.");
            throw new InvalidOperationException($"Docker is unavailable while constructing {image}.");
        }

        var fixture = new PostgresLockTestContainerFixture(
            "external-postgres", "external-redis", DockerUnavailable, DockerUnavailable);
        Assert.Equal("external-postgres", fixture.ConnectionString);
        Assert.Equal("external-redis", fixture.RedisConnectionString);
        await fixture.InitializeAsync();
        await fixture.DisposeAsync();
        Assert.Equal("external-postgres", fixture.ConnectionString);
        Assert.Equal("external-redis", fixture.RedisConnectionString);
    }

    [Fact]
    public async Task RedisIsDisposedEvenWhenOwnedPostgresDisposalFails()
    {
        var failure = new InvalidOperationException("PostgreSQL disposal failed.");
        var postgres = new RecordingContainer("owned-postgres") { DisposalFailure = failure };
        var redis = new RecordingContainer("owned-redis");
        var fixture = new PostgresLockTestContainerFixture(null, null, _ => postgres, _ => redis);

        await fixture.InitializeAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(fixture.DisposeAsync));
        Assert.Equal(1, postgres.Disposals);
        Assert.Equal(1, redis.Disposals);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RealContainersStartAndStopOnlyOwnedDependencies(bool externalPostgres, bool externalRedis)
    {
        // Borrow configured services when present. Without external settings this owner
        // supplies the independent services used to exercise mixed ownership.
        var borrowed = externalPostgres || externalRedis ? new PostgresLockTestContainerFixture() : null;
        try
        {
            if (borrowed is not null) await borrowed.InitializeAsync();
            var fixture = new PostgresLockTestContainerFixture(
                externalPostgres ? borrowed!.ConnectionString : null,
                externalRedis ? borrowed!.RedisConnectionString : null);
            string postgresConnectionString;
            string redisConnectionString;
            try
            {
                await fixture.InitializeAsync();
                postgresConnectionString = fixture.ConnectionString;
                redisConnectionString = fixture.RedisConnectionString;
                await ProbePostgresAsync(postgresConnectionString);
                await ProbeRedisAsync(redisConnectionString);
            }
            finally { await fixture.DisposeAsync(); }

            if (externalPostgres)
                await ProbePostgresAsync(postgresConnectionString);
            else
                await Assert.ThrowsAnyAsync<NpgsqlException>(() => ProbePostgresAsync(postgresConnectionString));
            if (externalRedis)
                await ProbeRedisAsync(redisConnectionString);
            else
                await Assert.ThrowsAnyAsync<RedisConnectionException>(() => ProbeRedisAsync(redisConnectionString));
        }
        finally { if (borrowed is not null) await borrowed.DisposeAsync(); }
    }

    private static async Task ProbePostgresAsync(string connectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, Timeout = 2, CommandTimeout = 2 }.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync(deadline.Token));
    }

    private static async Task ProbeRedisAsync(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = true;
        options.ConnectRetry = 0;
        options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 2000;
        using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        await redis.GetDatabase().PingAsync();
    }

    private sealed class RecordingContainer(string connectionString) : PostgresLockTestContainerFixture.IOwnedContainer
    {
        public int ConnectionStringReads { get; private set; }
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public Exception? DisposalFailure { get; init; }
        public string ConnectionString
        {
            get { ConnectionStringReads++; return connectionString; }
        }

        public Task StartAsync()
        {
            Starts++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return DisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalFailure);
        }
    }
}
