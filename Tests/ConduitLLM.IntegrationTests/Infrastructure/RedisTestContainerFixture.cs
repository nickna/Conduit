using System.Net;
using System.Net.Sockets;
using Testcontainers.Redis;
using Xunit;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>
/// Shared Redis container fixture for SignalR integration tests.
/// Provides a Redis container that is shared across all tests in a collection.
/// </summary>
public class RedisTestContainerFixture : IAsyncLifetime
{
    private RedisContainer _redisContainer = null!;
    private bool _isRunning;

    public string ConnectionString => _redisContainer.GetConnectionString();
    public string Host => _redisContainer.Hostname;
    public int Port => _redisContainer.GetMappedPublicPort(6379);
    public bool IsRunning => _isRunning;

    public async Task InitializeAsync()
    {
        var hostPort = GetAvailableHostPort();
        _redisContainer = new RedisBuilder("redis:7.4-alpine")
            .WithName($"redis-signalr-test-{Guid.NewGuid():N}")
            .WithPortBinding(hostPort, 6379)
            .Build();

        await _redisContainer.StartAsync();
        _isRunning = true;
    }

    public async Task DisposeAsync()
    {
        if (_redisContainer != null)
        {
            await _redisContainer.DisposeAsync();
        }
    }

    /// <summary>
    /// Stops the Redis container to simulate failover scenario.
    /// </summary>
    public async Task StopAsync()
    {
        await _redisContainer.StopAsync();
        _isRunning = false;
    }

    /// <summary>
    /// Restarts the Redis container after a simulated failure.
    /// </summary>
    public async Task RestartAsync()
    {
        await _redisContainer.StartAsync();
        _isRunning = true;
    }

    /// <summary>
    /// Flushes all Redis data for test isolation.
    /// </summary>
    public async Task FlushAllAsync()
    {
        if (_isRunning)
        {
            await _redisContainer.ExecAsync(new[] { "redis-cli", "FLUSHALL" });
        }
    }

    private static int GetAvailableHostPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// xUnit collection definition for tests that share a Redis container.
/// </summary>
[CollectionDefinition("SignalR Redis Collection")]
public class SignalRRedisCollection : ICollectionFixture<RedisTestContainerFixture>
{
    // This class has no code - it's used to wire up the fixture with xUnit collection
}
