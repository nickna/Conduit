using System.Net;
using System.Linq;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Security.Options;
using ConduitLLM.Tests.Core.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Admin.Services;

[Collection("AdminMasterKeyEnvironment")]
[Trait("Category", "Unit")]
[Trait("Component", "AdminSecurity")]
public sealed class AdminSecurityRateLimitTests : IDisposable
{
    private const string MasterKey = "admin-rate-limit-test-key";
    private readonly string _originalMasterKey;

    public AdminSecurityRateLimitTests()
    {
        _originalMasterKey = Environment.GetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY");
        Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", MasterKey);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", _originalMasterKey);

    [SkippableFact]
    public async Task DistributedTracking_TwoAdminInstancesEnforceOneRedisQuota()
    {
        var redis = RedisTestServer.Require();
        var ipAddress = new IPAddress(Guid.NewGuid().ToByteArray());
        var key = $"rate_limit:admin-api:{ipAddress}";
        using var firstProvider = CreateProvider(useDistributedTracking: true, redis);
        using var secondProvider = CreateProvider(useDistributedTracking: true, redis);
        var first = firstProvider.GetRequiredService<SecurityService>();
        var second = secondProvider.GetRequiredService<SecurityService>();
        Assert.Same(first, firstProvider.GetRequiredService<IAdminSecurityService>());
        Assert.Same(first, firstProvider.GetRequiredService<ConduitLLM.Security.Interfaces.ISecurityService>());

        try
        {
            Assert.True((await first.IsRequestAllowedAsync(CreateRequest(ipAddress))).IsAllowed);
            Assert.True((await second.IsRequestAllowedAsync(CreateRequest(ipAddress))).IsAllowed);

            var firstDenied = await first.IsRequestAllowedAsync(CreateRequest(ipAddress));
            var secondDenied = await second.IsRequestAllowedAsync(CreateRequest(ipAddress));

            Assert.Equal(StatusCodes.Status429TooManyRequests, firstDenied.StatusCode);
            Assert.Equal(StatusCodes.Status429TooManyRequests, secondDenied.StatusCode);
            Assert.NotNull(firstDenied.RateLimit);
            Assert.Equal(2, firstDenied.RateLimit.Limit);
            Assert.Equal("ip", firstDenied.RateLimit.Scope);
            Assert.InRange((firstDenied.RateLimit.ResetsAt - DateTime.UtcNow).TotalSeconds, 50, 60);
            Assert.Equal(4L, (long)await redis.GetDatabase().StringGetAsync(key));
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task DistributedTracking_WithoutRedisUsesMemoryAndWarns()
    {
        var logger = new Mock<ILogger<SecurityService>>();
        // Even an IDistributedCache cannot supply the atomic increment Redis provides.
        using var firstProvider = CreateProvider(useDistributedTracking: true, logger: logger.Object);
        using var secondProvider = CreateProvider(useDistributedTracking: true);
        var ipAddress = IPAddress.Parse("203.0.113.91");

        foreach (var service in new[] { firstProvider, secondProvider }
            .Select(provider => provider.GetRequiredService<SecurityService>()))
        {
            Assert.True((await service.IsRequestAllowedAsync(CreateRequest(ipAddress))).IsAllowed);
            Assert.True((await service.IsRequestAllowedAsync(CreateRequest(ipAddress))).IsAllowed);
            Assert.Equal(StatusCodes.Status429TooManyRequests,
                (await service.IsRequestAllowedAsync(CreateRequest(ipAddress))).StatusCode);
        }

        logger.Verify(log => log.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString().Contains("no Redis connection is registered")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once);
    }

    [Fact]
    public async Task DistributedTrackingDisabled_UsesIndependentMemoryQuotasWithoutTouchingRedis()
    {
        // A Redis registration may connect lazily when resolved. Disabled tracking must
        // avoid that connection attempt, as well as avoiding counter operations.
        static IConnectionMultiplexer UnexpectedRedisResolution(IServiceProvider _) =>
            throw new InvalidOperationException("Disabled distributed tracking must not resolve Redis");
        using var firstProvider = CreateProvider(useDistributedTracking: false,
            redisFactory: UnexpectedRedisResolution);
        using var secondProvider = CreateProvider(useDistributedTracking: false,
            redisFactory: UnexpectedRedisResolution);
        var ipAddress = IPAddress.Parse("203.0.113.92");

        foreach (var service in new[] { firstProvider, secondProvider }
            .Select(provider => provider.GetRequiredService<SecurityService>()))
        {
            Assert.True((await service.IsRequestAllowedAsync(CreateRequest(ipAddress))).IsAllowed);
            Assert.True((await service.IsRequestAllowedAsync(CreateRequest(ipAddress))).IsAllowed);
            Assert.Equal(StatusCodes.Status429TooManyRequests,
                (await service.IsRequestAllowedAsync(CreateRequest(ipAddress))).StatusCode);
        }
    }

    private static ServiceProvider CreateProvider(
        bool useDistributedTracking,
        IConnectionMultiplexer redis = null,
        ILogger<SecurityService> logger = null,
        Func<IServiceProvider, IConnectionMultiplexer> redisFactory = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(logger ?? NullLogger<SecurityService>.Instance);
        // Resolve the production registration, including its optional Redis dependency.
        services.AddAdminServices(configuration);
        services.PostConfigure<AdminSecurityOptions>(options =>
        {
            options.UseDistributedTracking = useDistributedTracking;
            options.FailedAuth.Enabled = false;
            options.IpFiltering.Enabled = false;
            options.RateLimiting.Enabled = true;
            options.RateLimiting.MaxRequests = 2;
            options.RateLimiting.WindowSeconds = 60;
        });
        services.AddDistributedMemoryCache();
        if (redis is not null)
        {
            services.AddSingleton(redis);
        }
        if (redisFactory is not null)
        {
            services.AddSingleton(redisFactory);
        }
        return services.BuildServiceProvider();
    }

    private static HttpContext CreateRequest(IPAddress ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test";
        context.Request.Headers["X-API-Key"] = MasterKey;
        context.Connection.RemoteIpAddress = ipAddress;
        return context;
    }
}
