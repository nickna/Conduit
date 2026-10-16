#nullable enable

using System.Net;

using ConduitLLM.Configuration.Utilities;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services;

[Collection("AdminCompositionEnvironment")]
public sealed class RedisUrlParserTests
{
    [Theory]
    [InlineData("redis", false)]
    [InlineData("rediss", true)]
    [InlineData("REDISS", true)]
    public void UriAclCredentialsAndDatabase_AreDecodedIntoTypedOptions(string scheme, bool ssl)
    {
        var options = RedisUrlParser.ParseConfiguration(
            scheme + "://user%3Aname:p%40ss%3Aword%3Bvalue@cache.invalid:6381/2");

        AssertEndpoint(options, "cache.invalid", 6381);
        Assert.Equal("user:name", options.User);
        Assert.Equal("p@ss:word;value", options.Password);
        Assert.Equal(2, options.DefaultDatabase);
        Assert.Equal(ssl, options.Ssl);
        Assert.False(options.AbortOnConnectFail);
        Assert.Equal(10000, options.ConnectTimeout);
        Assert.Equal(10000, options.SyncTimeout);
        var serialized = ConfigurationOptions.Parse(options.ToString(includePassword: true));
        Assert.Equal(options.User, serialized.User);
        Assert.Equal(options.Password, serialized.Password);
        Assert.Equal(options.DefaultDatabase, serialized.DefaultDatabase);
    }

    [Fact]
    public void UriEscapes_AreDecodedOnceAndPlusRemainsLiteral()
    {
        var options = RedisUrlParser.ParseConfiguration("redis://user%253Aname:pass%252Cword+plus@cache.invalid");

        Assert.Equal("user%3Aname", options.User);
        Assert.Equal("pass%2Cword+plus", options.Password);
        AssertEndpoint(options, "cache.invalid", 6379);
    }

    [Theory]
    [InlineData("redis://user%20name:pass%20word@cache.invalid", "user name", "pass word")]
    [InlineData("cache.invalid:6379,user=user%3Aname,password=pass%2Cword", "user%3Aname", "pass%2Cword")]
    public void InternalEncodedSpacesAndRawLiteralPercentValues_RoundTripWithoutTrimmingOrDecodingTwice(
        string value, string user, string password)
    {
        var options = RedisUrlParser.ParseConfiguration(value);
        var serialized = ConfigurationOptions.Parse(options.ToString(includePassword: true));

        Assert.Equal(user, options.User);
        Assert.Equal(password, options.Password);
        Assert.Equal(user, serialized.User);
        Assert.Equal(password, serialized.Password);
    }

    [Theory]
    [InlineData("redis://:password@cache.invalid")]
    [InlineData("redis://:password@cache.invalid/")]
    public void PasswordOnlyAuthentication_IsSupportedWithoutAnAclUser(string value)
    {
        var options = RedisUrlParser.ParseConfiguration(value);

        Assert.Equal("password", options.Password);
        Assert.True(string.IsNullOrEmpty(options.User));
        AssertEndpoint(options, "cache.invalid", 6379);
    }

    [Theory]
    [InlineData("redis://[::1]:6382/0", "::1", 6382, 0)]
    [InlineData("rediss://:password@[2001:db8::1]:6383/12", "2001:db8::1", 6383, 12)]
    public void Ipv6UriAndDatabasePath_ArePreserved(string value, string host, int port, int database)
    {
        var options = RedisUrlParser.ParseConfiguration(value);

        AssertEndpoint(options, host, port);
        Assert.Equal(database, options.DefaultDatabase);
    }

    [Fact]
    public void SupportedUriQueryOptions_UseTypedValuesAndPreserveTlsIdentity()
    {
        var options = RedisUrlParser.ParseConfiguration("rediss://user:password@cache.invalid:6380/3" +
            "?connectTimeout=4321&syncTimeout=2345&asyncTimeout=3456&connectRetry=3&keepAlive=15&abortConnect=true&sslHost=cache.internal");

        Assert.True(options.Ssl);
        Assert.Equal("cache.internal", options.SslHost);
        Assert.Equal(3, options.DefaultDatabase);
        Assert.Equal(4321, options.ConnectTimeout);
        Assert.Equal(2345, options.SyncTimeout);
        Assert.Equal(3456, options.AsyncTimeout);
        Assert.Equal(3, options.ConnectRetry);
        Assert.Equal(15, options.KeepAlive);
        Assert.True(options.AbortOnConnectFail);
    }

    [Fact]
    public void UriQueryNames_AreCaseInsensitiveAndFillOnlyMissingTimeoutDefaults()
    {
        var options = RedisUrlParser.ParseConfiguration(
            "redis://cache.invalid?CONNECTTIMEOUT=4321&SSL=true&SSLHOST=certificate.internal&CONNECTRETRY=0&KEEPALIVE=0");

        Assert.Equal(4321, options.ConnectTimeout);
        Assert.Equal(10000, options.SyncTimeout);
        Assert.True(options.Ssl);
        Assert.Equal("certificate.internal", options.SslHost);
        Assert.Equal(0, options.ConnectRetry);
        Assert.Equal(0, options.KeepAlive);
    }

    [Theory]
    [InlineData("cache.invalid", 6379)]
    [InlineData("cache.invalid:6384", 6384)]
    [InlineData("cache.invalid:6384,connectTimeout=4321,syncTimeout=2345", 6384)]
    public void PlainEndpointsAndOptions_AreNotRewrittenByAppendingAPortToTheLastOption(string input, int port)
    {
        var options = RedisUrlParser.ParseConfiguration(input);
        var compatibility = ConfigurationOptions.Parse(RedisUrlParser.ParseRedisUrl(input));

        AssertEndpoint(options, "cache.invalid", port);
        AssertEndpoint(compatibility, "cache.invalid", port);
        Assert.False(options.AbortOnConnectFail);
        Assert.Equal(input.Contains("connectTimeout") ? 4321 : 10000, options.ConnectTimeout);
        Assert.Equal(input.Contains("syncTimeout") ? 2345 : 10000, options.SyncTimeout);
    }

    [Fact]
    public void RawOptions_PreserveExplicitDefaultsTlsAclAndMultipleEndpoints()
    {
        var options = RedisUrlParser.ParseConfiguration(
            "cache.invalid:6380,[::1]:6381,user=acl-user,password=p;word,ssl=true,sslHost=cache.internal," +
            "defaultDatabase=4,abortConnect=true,connectTimeout=4321,syncTimeout=2345,asyncTimeout=3456");

        Assert.Equal(2, options.EndPoints.Count);
        Assert.Equal("acl-user", options.User);
        Assert.Equal("p;word", options.Password);
        Assert.True(options.Ssl);
        Assert.Equal("cache.internal", options.SslHost);
        Assert.Equal(4, options.DefaultDatabase);
        Assert.True(options.AbortOnConnectFail);
        Assert.Equal(4321, options.ConnectTimeout);
        Assert.Equal(2345, options.SyncTimeout);
        Assert.Equal(3456, options.AsyncTimeout);
        var compatibility = ConfigurationOptions.Parse(RedisUrlParser.ParseRedisUrl(options.ToString(includePassword: true)));
        Assert.Equal(options.Password, compatibility.Password);
        Assert.Equal(2, compatibility.EndPoints.Count);
        Assert.Equal(options.SslHost, compatibility.SslHost);
    }

    [Theory]
    [InlineData("redis://cache.invalid/-1")]
    [InlineData("redis://cache.invalid/not-a-number")]
    [InlineData("redis://cache.invalid/1/2")]
    [InlineData("redis://cache.invalid/2147483648")]
    [InlineData("redis://cache.invalid/1?defaultDatabase=2")]
    [InlineData("redis://cache.invalid?unknownOption=value")]
    [InlineData("redis://cache.invalid?connectTimeout=1&CONNECTTIMEOUT=2")]
    [InlineData("redis://cache.invalid?connectTimeout=bad")]
    [InlineData("redis://cache.invalid?connectTimeout=0")]
    [InlineData("redis://cache.invalid?syncTimeout=-1")]
    [InlineData("redis://cache.invalid?connectRetry=-1")]
    [InlineData("redis://cache.invalid?abortConnect=bad")]
    [InlineData("redis://cache.invalid?connectTimeout")]
    [InlineData("redis://cache.invalid?password=override")]
    [InlineData("redis://cache.invalid?user=override")]
    [InlineData("rediss://cache.invalid?ssl=false")]
    [InlineData("redis://cache.invalid#fragment")]
    [InlineData("redis://cache.invalid:0")]
    [InlineData("redis://cache.invalid:65536")]
    [InlineData("redis://cache.invalid:bad")]
    [InlineData("redis://cache.invalid:")]
    [InlineData("redis://")]
    [InlineData("http://cache.invalid:6379")]
    [InlineData("cache.invalid:6379,unknownOption=value")]
    [InlineData("cache.invalid:6379,connectTimeout=bad")]
    [InlineData("cache.invalid:6379,password=one\nssl=false")]
    public void UnsupportedOrMalformedInputs_AreRejectedBeforeConnectionAttempts(string input)
    {
        var error = Assert.Throws<ArgumentException>(() => RedisUrlParser.ParseConfiguration(input));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(input, error.ToString());
        Assert.Throws<ArgumentException>(() => RedisUrlParser.ParseRedisUrl(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyInputs_AreRejectedByBothParserAdapters(string? input)
    {
        Assert.Throws<ArgumentException>(() => RedisUrlParser.ParseConfiguration(input!));
        Assert.Throws<ArgumentException>(() => RedisUrlParser.ParseRedisUrl(input!));
    }

    [Theory]
    [InlineData("redis://user:dummy%2Cssl%3Dfalse@cache.invalid")]
    [InlineData("redis://dummy%2Cssl%3Dfalse:password@cache.invalid")]
    [InlineData("redis://user:dummy%0Asecret@cache.invalid")]
    [InlineData("redis://user:dummy%00secret@cache.invalid")]
    [InlineData("redis://user:dummy%7Fsecret@cache.invalid")]
    [InlineData("redis://user:%20dummy-secret@cache.invalid")]
    [InlineData("redis://user:dummy-secret%20@cache.invalid")]
    [InlineData("redis://%20dummy-user:password@cache.invalid")]
    [InlineData("redis://dummy-user%20:password@cache.invalid")]
    [InlineData("redis://user:dummy%2@cache.invalid")]
    [InlineData("redis://user:dummy%GGsecret@cache.invalid")]
    [InlineData("redis://user:dummy secret@cache.invalid")]
    [InlineData("redis://dummy-user@cache.invalid")]
    [InlineData("redis://dummy-user:@cache.invalid")]
    [InlineData("redis://:@cache.invalid")]
    [InlineData("dummy-user:dummy-secret@cache.invalid:6379")]
    public void UnsafeOrAmbiguousAuthentication_IsRejectedWithCredentialFreeDiagnostics(string input)
    {
        var exception = Assert.Throws<ArgumentException>(() => RedisUrlParser.ParseConfiguration(input));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(input, exception.ToString());
        Assert.DoesNotContain("dummy", exception.ToString());
    }

    [Fact]
    public void Resolver_PrefersAValidPrimaryAndDoesNotInspectAnInvalidFallback()
    {
        var options = RedisUrlParser.ResolveConfiguration("rediss://user:password@primary.invalid/2", "invalid://dummy-secret")!;

        AssertEndpoint(options, "primary.invalid", 6379);
        Assert.Equal(2, options.DefaultDatabase);
        Assert.True(options.Ssl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Resolver_UsesTheSameContractForFallbackWhenPrimaryIsAbsentOrBlank(string? primary)
    {
        var options = RedisUrlParser.ResolveConfiguration(primary,
            "cache.invalid:6380,password=fallback-secret,ssl=true,sslHost=cache.internal,connectTimeout=4321")!;

        AssertEndpoint(options, "cache.invalid", 6380);
        Assert.Equal("fallback-secret", options.Password);
        Assert.True(options.Ssl);
        Assert.Equal("cache.internal", options.SslHost);
        Assert.Equal(4321, options.ConnectTimeout);
    }

    [Fact]
    public void Resolver_RejectsAnInvalidNonblankPrimaryRatherThanSilentlySwitchingCredentials()
    {
        var exception = Assert.Throws<ArgumentException>(() => RedisUrlParser.ResolveConfiguration(
            "redis://user:dummy%2Cssl%3Dfalse@primary.invalid", "fallback.invalid:6379,password=valid"));

        Assert.DoesNotContain("dummy", exception.ToString());
    }

    [Fact]
    public void Resolver_WithNeitherConfiguration_ReturnsNull()
    {
        Assert.Null(RedisUrlParser.ResolveConfiguration(null, null));
        Assert.Null(RedisUrlParser.ResolveConfiguration(" ", " "));
    }

    [Fact]
    public void EnvironmentAndStringAdapters_UseTheSameResolutionAndSerializationContract()
    {
        using var environment = new RedisEnvironment("redis://user:password@cache.invalid/3?syncTimeout=2345", "other.invalid:6379");

        var typed = RedisUrlParser.ResolveConfiguration()!;
        var compatibility = ConfigurationOptions.Parse(RedisUrlParser.ResolveConnectionString()!);

        AssertEndpoint(typed, "cache.invalid", 6379);
        AssertEndpoint(compatibility, "cache.invalid", 6379);
        Assert.Equal("password", compatibility.Password);
        Assert.Equal(3, compatibility.DefaultDatabase);
        Assert.Equal(2345, compatibility.SyncTimeout);
    }

    [Fact]
    public void EnvironmentAdapters_DoNotSilentlyFallBackFromAnInvalidPrimary()
    {
        using var environment = new RedisEnvironment("redis://user:dummy%2Cssl%3Dfalse@cache.invalid", "fallback.invalid:6379");

        Assert.Throws<ArgumentException>(() => RedisUrlParser.ResolveConfiguration());
        Assert.Throws<ArgumentException>(() => RedisUrlParser.ResolveConnectionString());
    }

    private static void AssertEndpoint(ConfigurationOptions options, string host, int port)
    {
        var endpoint = Assert.Single(options.EndPoints);
        var actual = endpoint switch
        {
            DnsEndPoint dns => (dns.Host, dns.Port),
            IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            _ => throw new InvalidOperationException("Unexpected test endpoint type.")
        };
        Assert.Equal(host, actual.Item1);
        Assert.Equal(port, actual.Item2);
    }

    private sealed class RedisEnvironment : IDisposable
    {
        private readonly string? _url = Environment.GetEnvironmentVariable("REDIS_URL");
        private readonly string? _connection = Environment.GetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING");
        public RedisEnvironment(string? url, string? connection)
        {
            Environment.SetEnvironmentVariable("REDIS_URL", url);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", connection);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("REDIS_URL", _url);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", _connection);
        }
    }
}
