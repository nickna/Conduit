using System.Text.Json;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Security.Models;
using ConduitLLM.Security.Options;
using ConduitLLM.Security.Serialization;
using ConduitLLM.Security.Services;
using ConduitLLM.Tests.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Security;

public sealed class FailedAuthServiceTests : IAsyncLifetime
{
    private readonly string _prefix = RedisTestServer.NewKeyPrefix("failed-auth-service");
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => RedisTestServer.CleanupAsync(_prefix);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionConstructors_TwoInstancesShareAtomicRedisFailuresAndBan(bool admin)
    {
        var redis = RedisTestServer.Require();
        using var first = Provider(admin, true, redis);
        using var second = Provider(admin, true, redis);
        var firstService = first.GetRequiredService<ConduitLLM.Security.Interfaces.ISecurityService>();
        var secondService = second.GetRequiredService<ConduitLLM.Security.Interfaces.ISecurityService>();
        var ip = _prefix + (admin ? "admin" : "gateway");
        const string secret = "secret-key-that-must-be-masked";
        await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
            (index % 2 == 0 ? firstService : secondService).RecordFailedAuthAsync(ip, secret)));
        Assert.True(await firstService.IsIpBannedAsync(ip));
        Assert.True(await secondService.IsIpBannedAsync(ip));
        var scope = admin ? "admin-api" : "core-api";
        var key = $"security:failed_auth:v2:{{{scope}:{ip}}}:ban";
        Assert.Equal(200, (long)await redis.GetDatabase().HashGetAsync(key, "count"));
        var masked = (await redis.GetDatabase().HashGetAsync(key, "lastKey")).ToString();
        Assert.NotEqual(secret, masked);
        Assert.DoesNotContain(secret, masked);
        Assert.Equal(scope, (await redis.GetDatabase().HashGetAsync(key, "source")).ToString());
        await firstService.ClearFailedAuthAttemptsAsync(ip);
        Assert.True(await secondService.IsIpBannedAsync(ip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistributedTrackingDisabled_DoesNotResolveLazyRedis_UsesIndependentMemory(bool admin)
    {
        static IConnectionMultiplexer UnexpectedRedis(IServiceProvider _) => throw new InvalidOperationException("Unexpected Redis resolution");
        using var first = Provider(admin, false, redisFactory: UnexpectedRedis);
        using var second = Provider(admin, false, redisFactory: UnexpectedRedis);
        var firstService = first.GetRequiredService<ConduitLLM.Security.Interfaces.ISecurityService>();
        var secondService = second.GetRequiredService<ConduitLLM.Security.Interfaces.ISecurityService>();
        for (var i = 0; i < 3; i++) await firstService.RecordFailedAuthAsync("ip", "secret");
        Assert.True(await firstService.IsIpBannedAsync("ip"));
        Assert.False(await secondService.IsIpBannedAsync("ip"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisNotRegistered_WarnsAndCountsLocallyInsteadOfUsingDistributedReadWrite(bool admin)
    {
        var logs = new List<string>();
        using var provider = Provider(admin, true, logs: logs);
        var service = provider.GetRequiredService<ConduitLLM.Security.Interfaces.ISecurityService>();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => service.RecordFailedAuthAsync("ip"))));
        Assert.True(await service.IsIpBannedAsync("ip"));
        Assert.Contains(logs, entry => entry.Contains("Distributed failed-auth tracking") && entry.Contains("per instance only"));
    }

    [Fact]
    public async Task DisabledFailedAuth_TrackerFactoryDoesNotResolveRedis_AndServiceTouchesNoCache()
    {
        var redisResolutions = 0;
        using var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_ =>
        {
            redisResolutions++;
            throw new InvalidOperationException("Unexpected Redis resolution");
        }).BuildServiceProvider();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var options = new SecurityOptionsBase { UseDistributedTracking = true, FailedAuth = { Enabled = false } };
        var tracker = FailedAuthTrackerFactory.Create(options, services, memory, NullLogger.Instance);
        var distributed = new Mock<IDistributedCache>(MockBehavior.Strict);
        var service = new TestService(options, memory, distributed.Object, tracker);
        await service.RecordFailedAuthAsync("ip");
        await service.ClearFailedAuthAttemptsAsync("ip");
        Assert.False(await service.IsIpBannedAsync("ip"));
        Assert.Equal(0, redisResolutions);
        distributed.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBansRemainVisible_ExpireByDeadline_AndSuccessDoesNotClearBan(bool distributed)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var options = new SecurityOptionsBase { UseDistributedTracking = distributed };
        var service = new TestService(options, memory, cache, new MemoryFailedAuthTracker(memory));
        var ban = new BannedIpInfo { BannedUntil = DateTime.UtcNow.AddMinutes(5), FailedAttempts = 3, Source = "test" };
        if (distributed)
            await cache.SetStringAsync("ban:legacy", JsonSerializer.Serialize(ban, SecurityCacheJsonContext.Default.BannedIpInfo));
        else
            memory.Set("ban:legacy", ban);
        Assert.True(await service.IsIpBannedAsync("legacy"));
        await service.ClearFailedAuthAttemptsAsync("legacy");
        Assert.True(await service.IsIpBannedAsync("legacy"));
        ban.BannedUntil = DateTime.UtcNow.AddMinutes(-1);
        if (distributed)
            await cache.SetStringAsync("ban:legacy", JsonSerializer.Serialize(ban, SecurityCacheJsonContext.Default.BannedIpInfo));
        else
            memory.Set("ban:legacy", ban);
        Assert.False(await service.IsIpBannedAsync("legacy"));
    }

    [Fact]
    public async Task SuccessfulAuthenticationClear_StartsNewAttemptWindow_AndNewBanIsMirroredForLegacyReaders()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var options = new SecurityOptionsBase { UseDistributedTracking = true, FailedAuth = { MaxAttempts = 3 } };
        var service = new TestService(options, memory, cache, new MemoryFailedAuthTracker(memory));
        await service.RecordFailedAuthAsync("ip", "private-credential");
        await service.RecordFailedAuthAsync("ip", "private-credential");
        await service.ClearFailedAuthAttemptsAsync("ip");
        await service.RecordFailedAuthAsync("ip", "private-credential");
        Assert.False(await service.IsIpBannedAsync("ip"));
        await service.RecordFailedAuthAsync("ip", "private-credential");
        await service.RecordFailedAuthAsync("ip", "private-credential");
        var legacy = JsonSerializer.Deserialize((await cache.GetStringAsync("ban:ip"))!, SecurityCacheJsonContext.Default.BannedIpInfo);
        Assert.NotNull(legacy);
        Assert.Equal(3, legacy.FailedAttempts);
        Assert.Equal("test", legacy.Source);
        Assert.NotEqual("private-credential", legacy.LastAttemptedKey);
    }

    private static ServiceProvider Provider(bool admin, bool distributed, IConnectionMultiplexer? redis = null,
        Func<IServiceProvider, IConnectionMultiplexer>? redisFactory = null, List<string>? logs = null)
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(builder => { if (logs is not null) builder.AddProvider(new ListLoggerProvider(logs)); });
        services.AddMemoryCache();
        services.AddDistributedMemoryCache();
        if (admin)
        {
            services.AddAdminServices(config);
            services.PostConfigure<AdminSecurityOptions>(options =>
            {
                options.UseDistributedTracking = distributed;
                options.FailedAuth.Enabled = true;
                options.FailedAuth.MaxAttempts = 3;
                options.RateLimiting.Enabled = false;
                options.IpFiltering.Enabled = false;
            });
        }
        else
        {
            services.AddCoreApiSecurity(config);
            services.PostConfigure<GatewaySecurityOptions>(options =>
            {
                options.UseDistributedTracking = distributed;
                options.FailedAuth.Enabled = true;
                options.FailedAuth.MaxAttempts = 3;
                options.RateLimiting.Enabled = false;
                options.IpFiltering.Enabled = false;
            });
        }
        if (redis is not null) services.AddSingleton(redis);
        if (redisFactory is not null) services.AddSingleton(redisFactory);
        return services.BuildServiceProvider();
    }

    private sealed class TestService(SecurityOptionsBase options, IMemoryCache memory, IDistributedCache cache,
        IFailedAuthTracker tracker) : SecurityServiceBase(NullLogger.Instance, memory, cache, failedAuthTracker: tracker)
    {
        protected override string ServiceName => "test";
        protected override SecurityOptionsBase Options => options;
        public override Task<SecurityCheckResult> IsRequestAllowedAsync(HttpContext context) => Task.FromResult(SecurityCheckResult.Allowed());
    }

    private sealed class ListLoggerProvider(List<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(entries);
        public void Dispose() { }
        private sealed class ListLogger(List<string> values) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { lock (values) values.Add(formatter(state, exception)); }
        }
    }
}
