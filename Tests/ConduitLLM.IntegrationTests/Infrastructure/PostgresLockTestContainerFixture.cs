using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container for advisory-lock integration tests.
/// </summary>
public sealed class PostgresLockTestContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("conduit")
        .WithUsername("conduit")
        .WithPassword("conduitpass")
        .Build();

    private readonly string? _externalConnectionString = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
    private readonly string? _externalRedisConnectionString = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_REDIS");
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7.4-alpine").Build();

    public string ConnectionString => _externalConnectionString ?? _container.GetConnectionString();
    public string RedisConnectionString => _externalRedisConnectionString ?? _redis.GetConnectionString();

    public Task InitializeAsync() => Task.WhenAll(
        _externalConnectionString is null ? _container.StartAsync() : Task.CompletedTask,
        _externalRedisConnectionString is null ? _redis.StartAsync() : Task.CompletedTask);

    public async Task DisposeAsync()
    {
        try { await _container.DisposeAsync(); }
        finally { await _redis.DisposeAsync(); }
    }
}

[CollectionDefinition("Postgres advisory locks")]
public sealed class PostgresLockCollection :
    ICollectionFixture<PostgresLockTestContainerFixture>;
