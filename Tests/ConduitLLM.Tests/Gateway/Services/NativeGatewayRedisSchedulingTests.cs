using ConduitLLM.Configuration.Services;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Gateway.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Gateway.Services;

[Collection("AdminCompositionEnvironment")]
public sealed class NativeGatewayRedisSchedulingTests
{
    [Theory]
    [InlineData("rediss://scheduler-user:synthetic-password@localhost:6380/3?connectTimeout=1234&syncTimeout=2345&asyncTimeout=3456&abortConnect=true")]
    [InlineData("localhost:6379,user=scheduler-user,password=synthetic-password,defaultDatabase=3,connectTimeout=1234,syncTimeout=2345,asyncTimeout=3456,abortConnect=true,connectRetry=2")]
    public void NativeSchedulerPreservesConnectionConfiguration(string input)
    {
        var options = RedisUrlParser.ParseConfiguration(input);
        var serialized = options.ToString(includePassword: true);

        NativeGatewayRedisConnectionFactory.ConfigureSocketScheduling(options);

        Assert.Same(SocketManager.ThreadPool, options.SocketManager);
        Assert.Equal(serialized, options.ToString(includePassword: true));
        Assert.Equal("scheduler-user", options.User);
        Assert.Equal("synthetic-password", options.Password);
        Assert.Equal(3, options.DefaultDatabase);
        Assert.Equal(1234, options.ConnectTimeout);
        Assert.Equal(2345, options.SyncTimeout);
        Assert.Equal(3456, options.AsyncTimeout);
        Assert.True(options.AbortOnConnectFail);
    }

    [Fact]
    public void NativeSchedulerPreservesExplicitSocketManager()
    {
        using var explicitlySelected = new SocketManager("explicit-native-scheduler-test", 1);
        var options = new ConfigurationOptions { SocketManager = explicitlySelected };
        options.EndPoints.Add("localhost", 6379);

        NativeGatewayRedisConnectionFactory.ConfigureSocketScheduling(options);

        Assert.Same(explicitlySelected, options.SocketManager);
    }

    [Fact]
    public void GatewayCompositionSelectsSchedulingOnlyForNativeAndKeepsSeparateCacheOwnership()
    {
        WithRedisEnvironment("localhost:6379,user=scheduler-user,password=synthetic-password,defaultDatabase=3,connectTimeout=1234,syncTimeout=2345,asyncTimeout=3456,abortConnect=true", () =>
        {
            var builder = CreateBuilder();
            global::Program.ConfigureCachingServices(builder);
            using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
            var factory = provider.GetRequiredService<RedisConnectionFactory>();
            var cacheOptions = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;

#if CONDUIT_NATIVE_AOT
            Assert.IsType<NativeGatewayRedisConnectionFactory>(factory);
            Assert.Same(SocketManager.ThreadPool, cacheOptions.ConfigurationOptions.SocketManager);
            var configured = cacheOptions.ConfigurationOptions;
#else
            Assert.IsType<RedisConnectionFactory>(factory);
            Assert.Null(cacheOptions.ConfigurationOptions);
            var configured = ConfigurationOptions.Parse(cacheOptions.Configuration);
            Assert.Null(configured.SocketManager);
#endif
            Assert.Equal("scheduler-user", configured.User);
            Assert.Equal("synthetic-password", configured.Password);
            Assert.Equal(3, configured.DefaultDatabase);
            Assert.Equal(1234, configured.ConnectTimeout);
            Assert.Equal(2345, configured.SyncTimeout);
            Assert.Equal(3456, configured.AsyncTimeout);
            Assert.True(configured.AbortOnConnectFail);
            Assert.Equal(RedisUrlParser.ResolveConnectionString(), cacheOptions.Configuration);
            Assert.Equal("conduit-tasks:", cacheOptions.InstanceName);
            // RedisCache constructs and owns its own multiplexer instead of borrowing the factory's.
            Assert.Null(cacheOptions.ConnectionMultiplexerFactory);
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GatewayCompositionPreservesPriorOptionsAndExplicitSchedulerWithoutMutatingCaller(bool explicitScheduler)
    {
        using var customScheduler = new SocketManager("prior-gateway-scheduler-test", 1);
        var prior = ConfigurationOptions.Parse("127.0.0.1:6390,user=prior-user,password=prior-password,defaultDatabase=9,connectTimeout=4321,syncTimeout=5432,asyncTimeout=6543,abortConnect=false");
        prior.SocketManager = explicitScheduler ? customScheduler : null;
        var serialized = prior.ToString(includePassword: true);
        Func<Task<IConnectionMultiplexer>> priorFactory = () => throw new InvalidOperationException("Composition must not connect.");
        WithRedisEnvironment("localhost:6379,user=environment-user,password=environment-password,defaultDatabase=3", () =>
        {
            var builder = CreateBuilder();
            builder.Services.Configure<RedisCacheOptions>(options =>
            {
                options.ConfigurationOptions = prior;
                options.ConnectionMultiplexerFactory = priorFactory;
            });
            global::Program.ConfigureCachingServices(builder);
            using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
            var options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
            var configured = options.ConfigurationOptions;

            Assert.Equal(serialized, prior.ToString(includePassword: true));
            Assert.Same(explicitScheduler ? customScheduler : null, prior.SocketManager);
#if CONDUIT_NATIVE_AOT
            Assert.NotSame(prior, configured);
            Assert.Same(explicitScheduler ? customScheduler : SocketManager.ThreadPool, configured.SocketManager);
#else
            Assert.Same(prior, configured);
            Assert.Same(explicitScheduler ? customScheduler : null, configured.SocketManager);
#endif
            Assert.Equal(serialized, configured.ToString(includePassword: true));
            Assert.Equal("prior-user", configured.User);
            Assert.Equal("prior-password", configured.Password);
            Assert.Equal(9, configured.DefaultDatabase);
            Assert.Equal(4321, configured.ConnectTimeout);
            Assert.Equal(5432, configured.SyncTimeout);
            Assert.Equal(6543, configured.AsyncTimeout);
            Assert.False(configured.AbortOnConnectFail);
            Assert.Equal(RedisUrlParser.ResolveConnectionString(), options.Configuration);
            Assert.Equal("conduit-tasks:", options.InstanceName);
            Assert.Same(priorFactory, options.ConnectionMultiplexerFactory);
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        });
    }

    [SkippableFact]
    public async Task DisposingHostDistributedCacheLeavesSharedFactoryConnectionUsable()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(redis), "Set CONDUIT_CACHE_TEST_REDIS for real Redis connection ownership.");
        await WithRedisEnvironmentAsync(redis, async () =>
        {
            var builder = CreateBuilder();
            global::Program.ConfigureCachingServices(builder);
            using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
            var shared = provider.GetRequiredService<IConnectionMultiplexer>();
            var cache = Assert.IsAssignableFrom<RedisCache>(provider.GetRequiredService<IDistributedCache>());
            // A missing-key read creates the independently owned cache connection without writing state.
            var key = $"scheduler-ownership-test:{Guid.NewGuid():N}";
            await cache.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(10));
            var databaseField = typeof(RedisCache).GetField("_cache",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(databaseField);
            var database = Assert.IsAssignableFrom<IDatabase>(databaseField.GetValue(cache));
            Assert.NotSame(shared, database.Multiplexer);

            cache.Dispose();

            Assert.Throws<ObjectDisposedException>(() => cache.Get(key));
            await shared.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(shared.IsConnected);
        });
    }

    private static WebApplicationBuilder CreateBuilder() => WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ApplicationName = typeof(global::Program).Assembly.GetName().Name,
        EnvironmentName = "Testing"
    });

    private static void WithRedisEnvironment(string redis, Action action)
    {
        var originalUrl = Environment.GetEnvironmentVariable("REDIS_URL");
        var originalConnection = Environment.GetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING");
        try
        {
            Environment.SetEnvironmentVariable("REDIS_URL", null);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", redis);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDIS_URL", originalUrl);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", originalConnection);
        }
    }

    private static async Task WithRedisEnvironmentAsync(string redis, Func<Task> action)
    {
        var originalUrl = Environment.GetEnvironmentVariable("REDIS_URL");
        var originalConnection = Environment.GetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING");
        try
        {
            Environment.SetEnvironmentVariable("REDIS_URL", null);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", redis);
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDIS_URL", originalUrl);
            Environment.SetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING", originalConnection);
        }
    }
}
