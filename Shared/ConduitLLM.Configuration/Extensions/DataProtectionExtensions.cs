using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using StackExchange.Redis;

namespace ConduitLLM.Configuration.Extensions;

/// <summary>Configures Data Protection using the host's DI-owned Redis connection.</summary>
public static class DataProtectionExtensions
{
    /// <summary>
    /// Persists keys in Redis when configured, otherwise uses the default local key store.
    /// Registration does not connect to Redis or construct an intermediate service provider.
    /// </summary>
    public static IServiceCollection AddRedisDataProtection(
        this IServiceCollection services,
        string? redisConnectionString,
        string applicationName = "Conduit")
    {
        services.AddDataProtection().SetApplicationName(applicationName);
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            return services;
        }

        // Gateway supplies its pooled connection before calling this extension. Admin
        // uses this lazy registration; the final host provider owns its disposal.
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));
        services.AddOptions<KeyManagementOptions>()
            .Configure<IConnectionMultiplexer>((options, redis) =>
                options.XmlRepository = new RedisXmlRepository(
                    () => redis.GetDatabase(), $"{applicationName}-DataProtection-Keys"));

        return services;
    }
}
