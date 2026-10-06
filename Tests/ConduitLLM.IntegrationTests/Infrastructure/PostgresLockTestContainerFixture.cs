using Testcontainers.PostgreSql;
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

    public string ConnectionString => _externalConnectionString ?? _container.GetConnectionString();

    public Task InitializeAsync() => _externalConnectionString is null ? _container.StartAsync() : Task.CompletedTask;

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Postgres advisory locks")]
public sealed class PostgresLockCollection :
    ICollectionFixture<PostgresLockTestContainerFixture>;
