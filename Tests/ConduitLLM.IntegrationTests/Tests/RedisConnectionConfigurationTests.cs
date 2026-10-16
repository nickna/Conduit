using System.Net;

using ConduitLLM.Configuration.Utilities;
using ConduitLLM.IntegrationTests.Infrastructure;

using StackExchange.Redis;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Admin database health pool")]
[Trait("Category", "Integration")]
[Trait("Component", "ConnectionConfiguration")]
public sealed class RedisConnectionConfigurationTests(PostgresLockTestContainerFixture fixture)
{
    [Theory]
    [InlineData("primary-uri")]
    [InlineData("primary-options")]
    [InlineData("fallback-uri")]
    [InlineData("fallback-options")]
    public async Task GatewayConfigurationAuthenticatesEscapedAclCredentialsAndSelectsTheDatabase(string source)
    {
        using var observer = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var user = "conduit_parser_" + suffix + "@;:";
        var password = "test-only@;:/%25=" + suffix;
        var key = "issue-1561:" + suffix;
        const int database = 2;
        await observer.GetDatabase().ExecuteAsync("ACL", "SETUSER", user, "on", ">" + password, "allcommands", "allkeys");
        try
        {
            var options = RedisUrlParser.ParseConfiguration(fixture.RedisConnectionString);
            options.User = user;
            options.Password = password;
            options.DefaultDatabase = database;
            options.AbortOnConnectFail = true;
            options.ConnectTimeout = 5000;
            options.SyncTimeout = 5000;
            var (host, port) = options.EndPoints.Single() switch
            {
                DnsEndPoint endpoint => (endpoint.Host, endpoint.Port),
                IPEndPoint endpoint => (endpoint.Address.ToString(), endpoint.Port),
                _ => throw new InvalidOperationException("The test fixture requires a TCP endpoint.")
            };
            var endpointHost = host.Contains(':') ? "[" + host + "]" : host;
            var uri = $"redis://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{endpointHost}:{port}/{database}" +
                      "?abortConnect=true&connectTimeout=5000&syncTimeout=5000";
            var configured = source.EndsWith("uri", StringComparison.Ordinal)
                ? uri : options.ToString(includePassword: true);
            using var environment = new EnvironmentScope(new Dictionary<string, string?>
            {
                ["REDIS_URL"] = source.StartsWith("primary", StringComparison.Ordinal) ? configured : null,
                ["CONDUIT_REDIS_CONNECTION_STRING"] = source.StartsWith("fallback", StringComparison.Ordinal) ? configured : null
            });

            // Use the actual Gateway configuration entry point, then the same
            // serialized options consumed by its Redis-backed services.
            var canonical = global::Program.RequireRedisConfiguration();
            var parsed = ConfigurationOptions.Parse(canonical);
            Assert.Equal(user, parsed.User);
            Assert.Equal(password, parsed.Password);
            Assert.Equal(database, parsed.DefaultDatabase);
            using var client = await ConnectionMultiplexer.ConnectAsync(parsed);
            var selected = client.GetDatabase();
            Assert.Equal(database, selected.Database);
            Assert.True(await selected.StringSetAsync(key, "escaped credentials", TimeSpan.FromMinutes(1)));
            Assert.Equal("escaped credentials", (await observer.GetDatabase(database).StringGetAsync(key)).ToString());
            Assert.True((await observer.GetDatabase(0).StringGetAsync(key)).IsNull);
            await client.CloseAsync();
        }
        finally
        {
            await observer.GetDatabase(database).KeyDeleteAsync(key);
            await observer.GetDatabase().ExecuteAsync("ACL", "DELUSER", user);
        }
    }
}
