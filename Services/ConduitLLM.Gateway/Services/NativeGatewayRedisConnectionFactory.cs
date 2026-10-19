using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services;

/// <summary>Uses the managed thread pool for the Native Gateway's shared Redis connection.</summary>
internal sealed class NativeGatewayRedisConnectionFactory(
    IOptions<CacheOptions> options,
    ILogger<RedisConnectionFactory> logger) : RedisConnectionFactory(options, logger)
{
    /// <summary>Preserves connection configuration and explicit scheduler choices before connecting.</summary>
    protected override Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions options)
    {
        ConfigureSocketScheduling(options);
        return base.ConnectAsync(options);
    }

    /// <summary>Selects the shared thread-pool scheduler when no socket manager was specified.</summary>
    internal static void ConfigureSocketScheduling(ConfigurationOptions options) =>
        options.SocketManager ??= SocketManager.ThreadPool;
}
