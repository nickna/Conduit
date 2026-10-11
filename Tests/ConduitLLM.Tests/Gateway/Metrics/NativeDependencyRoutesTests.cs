using Npgsql;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Gateway.Metrics;

public sealed class NativeDependencyRoutesTests
{
    [Theory]
    [InlineData("postgres", "", 5432)]
    [InlineData("postgresql", ":5544", 5544)]
    public void PostgresRoute_PreservesUriCredentialsDatabaseAndOptions(string scheme, string port, int expectedPort)
    {
        var original = $"{scheme}://user:p%40ss@db.example{port}/db-name?Pooling=false&Timeout=9&Application%20Name=native%20test";
        var parsed = NativeDependencyRoutes.ParseDatabase(original);
        Assert.Equal(expectedPort, parsed.Port);
        Assert.Equal("p@ss", parsed.Password);
        Assert.Equal("native test", parsed.ApplicationName);
        var redirected = NativeDependencyRoutes.RedirectDatabaseUrl(original, 15105);
        var redirectedUri = new Uri(redirected);
        Assert.Equal(new Uri(original).UserInfo, redirectedUri.UserInfo);
        Assert.Equal(new Uri(original).Query, redirectedUri.Query);
        parsed = NativeDependencyRoutes.ParseDatabase(redirected);
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(15105, parsed.Port);
        Assert.Equal("db-name", parsed.Database);
        Assert.Equal("user", parsed.Username);
        Assert.Equal("p@ss", parsed.Password);
        Assert.False(parsed.Pooling);
        Assert.Equal(9, parsed.Timeout);
        Assert.Equal("native test", parsed.ApplicationName);
    }

    [Theory]
    [InlineData("postgresql://u:p@db.example/db?SSL%20Mode=VerifyFull")]
    [InlineData("postgresql://u:p@db.example/db?Host=other.example")]
    [InlineData("postgresql://u:p@db.example/db?Server=db.example")]
    [InlineData("postgresql://u:p@db.example/db?Port=9999")]
    public void PostgresRoute_RejectsTlsIdentityAndBypassOptions(string original) =>
        Assert.Throws<InvalidOperationException>(() => NativeDependencyRoutes.RedirectDatabaseUrl(original, 15105));

    [Theory]
    [InlineData("Host=first,second;Database=db;Username=u;Password=p")]
    [InlineData("Host=/tmp;Database=db;Username=u;Password=p")]
    public void PostgresProxy_RejectsNonSingleTcpTargets(string original) =>
        Assert.Throws<InvalidOperationException>(() => NativeDependencyRoutes.ValidateDatabaseTarget(new NpgsqlConnectionStringBuilder(original)));

    [Theory]
    [InlineData("redis://user:p%40ss@redis.example:6380/2?connectTimeout=12000&syncTimeout=2500", false)]
    [InlineData("rediss://user:p%40ss@redis.example:6380/2?connectTimeout=12000&syncTimeout=2500", true)]
    public void RedisRoute_PreservesAuthDatabaseTimeoutsAndTlsIdentity(string original, bool tls)
    {
        var parsed = NativeDependencyRoutes.ParseRedis(original, null);
        var redirected = ConfigurationOptions.Parse(NativeDependencyRoutes.RedirectRedis(parsed, 15106));
        Assert.Equal(("127.0.0.1", 15106), NativeDependencyRoutes.RedisTarget(redirected));
        Assert.Equal("user", redirected.User);
        Assert.Equal("p@ss", redirected.Password);
        Assert.Equal(2, redirected.DefaultDatabase);
        Assert.Equal(12000, redirected.ConnectTimeout);
        Assert.Equal(2500, redirected.SyncTimeout);
        Assert.Equal(tls, redirected.Ssl);
        if (tls) Assert.Equal("redis.example", redirected.SslHost);
        Assert.Equal(("redis.example", 6380), NativeDependencyRoutes.RedisTarget(parsed));
    }

    [Fact]
    public void RedisFallback_PreservesExplicitTlsIdentityAndDirectOptions()
    {
        var parsed = NativeDependencyRoutes.ParseRedis(null,
            "redis.example:6380,user=acl,password=secret,ssl=true,sslHost=certificate.example,defaultDatabase=4,connectRetry=7");
        var redirected = ConfigurationOptions.Parse(NativeDependencyRoutes.RedirectRedis(parsed, 15106));
        Assert.Equal("certificate.example", redirected.SslHost);
        Assert.Equal(4, redirected.DefaultDatabase);
        Assert.Equal(7, redirected.ConnectRetry);
        Assert.Equal("acl", redirected.User);
        Assert.Equal("secret", redirected.Password);
    }

    [Theory]
    [InlineData("redis://u:p%2Css@redis.example:6379")]
    [InlineData("redis://u:p%2Cssl%3Dfalse@redis.example:6379")]
    [InlineData("redis://u%2Cother:p@redis.example:6379")]
    [InlineData("rediss://redis.example:6379?sslHost=certificate.example%2Cssl%3Dfalse")]
    public void RedisUri_RejectsCommaSerializationAndOptionInjection(string original) =>
        Assert.Throws<InvalidOperationException>(() => NativeDependencyRoutes.ParseRedis(original, null));

    [Theory]
    [InlineData("redis://u:%20p%20@redis.example?connectTimeout=12000")]
    [InlineData("redis://%20u%20:p@redis.example")]
    public void RedisUri_RejectsCredentialWhitespaceBeforeSerializingOptions(string original) =>
        Assert.Throws<InvalidOperationException>(() => NativeDependencyRoutes.ParseRedis(original, null));

    [Theory]
    [InlineData("first:6379,second:6379")]
    [InlineData("first:26379,serviceName=sentinel-service")]
    public void RedisProxy_RejectsAlternateDiscoveredRoutes(string original) =>
        Assert.Throws<InvalidOperationException>(() => NativeDependencyRoutes.RedirectRedis(ConfigurationOptions.Parse(original), 15106));
}
