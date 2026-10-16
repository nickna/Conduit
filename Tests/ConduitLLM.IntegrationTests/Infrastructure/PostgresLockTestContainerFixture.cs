using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL and Redis dependencies for advisory-lock integration tests.
/// External services are borrowed; only missing dependencies have owned containers.
/// </summary>
public sealed class PostgresLockTestContainerFixture : IAsyncLifetime
{
    private readonly string? _externalConnectionString;
    private readonly string? _externalRedisConnectionString;
    private readonly IOwnedContainer? _container;
    private readonly IOwnedContainer? _redis;

    public PostgresLockTestContainerFixture()
        : this(Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES"),
            Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_REDIS")) { }

    internal PostgresLockTestContainerFixture(string? externalConnectionString, string? externalRedisConnectionString)
        : this(externalConnectionString, externalRedisConnectionString, CreatePostgresContainer, CreateRedisContainer) { }

    internal PostgresLockTestContainerFixture(
        string? externalConnectionString,
        string? externalRedisConnectionString,
        Func<string, IOwnedContainer> postgresFactory,
        Func<string, IOwnedContainer> redisFactory)
    {
        _externalConnectionString = externalConnectionString;
        _externalRedisConnectionString = externalRedisConnectionString;
        if (_externalConnectionString is null)
            _container = postgresFactory("postgres:17-alpine");
        if (_externalRedisConnectionString is null)
            _redis = redisFactory("redis:7.4-alpine");
    }

    public string ConnectionString => _externalConnectionString ?? _container!.ConnectionString;
    public string RedisConnectionString => _externalRedisConnectionString ?? _redis!.ConnectionString;

    public Task InitializeAsync() => Task.WhenAll(
        _container?.StartAsync() ?? Task.CompletedTask,
        _redis?.StartAsync() ?? Task.CompletedTask);

    public async Task DisposeAsync()
    {
        try { if (_container is not null) await _container.DisposeAsync(); }
        finally { if (_redis is not null) await _redis.DisposeAsync(); }
    }

    private static IOwnedContainer CreatePostgresContainer(string image)
    {
        var container = new PostgreSqlBuilder(image)
            .WithDatabase("conduit")
            .WithUsername("conduit")
            .WithPassword("conduitpass")
            .Build();
        return new OwnedContainer(container.GetConnectionString, () => container.StartAsync(), container.DisposeAsync);
    }

    private static IOwnedContainer CreateRedisContainer(string image)
    {
        var container = new RedisBuilder(image).Build();
        return new OwnedContainer(container.GetConnectionString, () => container.StartAsync(), container.DisposeAsync);
    }

    internal interface IOwnedContainer
    {
        string ConnectionString { get; }
        Task StartAsync();
        ValueTask DisposeAsync();
    }

    private sealed class OwnedContainer(
        Func<string> connectionString,
        Func<Task> startAsync,
        Func<ValueTask> disposeAsync) : IOwnedContainer
    {
        public string ConnectionString => connectionString();
        public Task StartAsync() => startAsync();
        public ValueTask DisposeAsync() => disposeAsync();
    }
}

[CollectionDefinition("Postgres advisory locks")]
public sealed class PostgresLockCollection :
    ICollectionFixture<PostgresLockTestContainerFixture>;
