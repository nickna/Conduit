using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Functions.Security;

using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace ConduitLLM.SchemaStartupFixtures;

/// <summary>Prepares encrypted fixture credentials with the hosts' production key ring.</summary>
public static class FunctionCredentialFixture
{
    /// <summary>Registers production credential protection with a bounded, DI-owned Redis connection.</summary>
    public static IServiceCollection AddServices(IServiceCollection services, string redisConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(redisConnectionString))
            throw new ArgumentException("A Redis connection string is required.", nameof(redisConnectionString));

        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.ConnectTimeout = 5000;
        options.SyncTimeout = 5000;
        options.AsyncTimeout = 5000;
        options.ConnectRetry = 0;
        options.AbortOnConnectFail = true;

        // The factory transfers connection ownership to the final service provider.
        // AddRedisDataProtection reuses it instead of creating another connection.
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        services.AddLogging();
        services.AddRedisDataProtection(redisConnectionString, "Conduit");
        services.AddSingleton<IFunctionCredentialProtector, FunctionCredentialProtector>();
        return services;
    }

    /// <summary>Protects a plaintext key without accepting opaque or oversized fixture values.</summary>
    public static string ProtectApiKey(IServiceProvider services, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new ArgumentException("A plaintext function API key is required.", nameof(plaintext));
        if (plaintext.StartsWith(FunctionCredentialProtector.Prefix, StringComparison.Ordinal))
            throw new ArgumentException("The fixture requires a plaintext function API key.", nameof(plaintext));

        var protectedKey = services.GetRequiredService<IFunctionCredentialProtector>().Protect(plaintext);
        if (protectedKey is null ||
            !protectedKey.StartsWith(FunctionCredentialProtector.Prefix, StringComparison.Ordinal) ||
            protectedKey.Length <= FunctionCredentialProtector.Prefix.Length ||
            string.Equals(protectedKey, plaintext, StringComparison.Ordinal) ||
            protectedKey.Length > 2000 ||
            protectedKey.Contains('\r') || protectedKey.Contains('\n'))
        {
            throw new InvalidOperationException("Function key protection did not produce a storable encrypted credential.");
        }

        return protectedKey;
    }
}
