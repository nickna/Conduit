using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container for advisory-lock integration tests.
/// </summary>
public sealed class PostgresLockTestContainerFixture : IAsyncLifetime
{
    private readonly string? _externalConnectionString = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
    private readonly string? _externalRedisConnectionString = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_REDIS");
    private readonly PostgreSqlContainer? _container;
    private readonly RedisContainer? _redis;

    public PostgresLockTestContainerFixture()
    {
        if (_externalConnectionString is null)
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("conduit")
                .WithUsername("conduit")
                .WithPassword("conduitpass")
                .Build();
        if (_externalRedisConnectionString is null)
            _redis = new RedisBuilder("redis:7.4-alpine").Build();
    }

    public string ConnectionString => _externalConnectionString ?? _container!.GetConnectionString();
    public string RedisConnectionString => _externalRedisConnectionString ?? _redis!.GetConnectionString();

    public Task InitializeAsync() => Task.WhenAll(
        _container?.StartAsync() ?? Task.CompletedTask,
        _redis?.StartAsync() ?? Task.CompletedTask);

    public async Task DisposeAsync()
    {
        try { if (_container is not null) await _container.DisposeAsync(); }
        finally { if (_redis is not null) await _redis.DisposeAsync(); }
    }
}

[CollectionDefinition("Postgres advisory locks")]
public sealed class PostgresLockCollection :
    ICollectionFixture<PostgresLockTestContainerFixture>;
