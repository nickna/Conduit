using System.Net;
using System.Net.Http.Json;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Admin.Endpoints;

public sealed class AdminRedisFeatureCompositionTests
{
    [Theory]
    [InlineData("GET", "/recent", null)]
    [InlineData("GET", "/summary", null)]
    [InlineData("GET", "/keys/1", null)]
    [InlineData("GET", "/stats", null)]
    [InlineData("GET", "/providers/1/key-errors", null)]
    [InlineData("POST", "/keys/1/clear", "{\"reenableKey\":false,\"confirmReenable\":false}")]
    [InlineData("POST", "/keys/1/disable?reason=repair", "\"repair\"")]
    public async Task ProviderErrorRoutes_WithoutRedis_ReturnUnavailableWithoutRepositoryWork(
        string method, string route, string body)
    {
        var keys = new Mock<IProviderKeyCredentialRepository>(MockBehavior.Strict);
        var providers = new Mock<IProviderRepository>(MockBehavior.Strict);
        using var host = AdminEndpointTestHost.Create(services =>
        {
            services.AddAdminRedisServices(redisConfigured: false);
            services.AddHttpContextAccessor();
            services.AddScoped(_ => keys.Object);
            services.AddScoped(_ => providers.Object);
            services.AddScoped(_ => Mock.Of<IEventPublisher>());
            services.AddScoped<ProviderErrorsEndpoints>();
        }, endpoints => ProviderErrorsEndpoints.MapProviderErrorsEndpoints(endpoints), validateServiceProvider: true);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/v1/admin/provider-errors" + route);
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<AdminProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(503, problem.Status);
        Assert.Equal(AdminErrorCodes.ServiceUnavailable, problem.Code);
        Assert.Contains("Redis is not configured", problem.Detail);
        Assert.Equal(response.Headers.GetValues("x-request-id").Single(), problem.TraceId);
        keys.VerifyNoOtherCalls();
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public void RedisConfigured_RegistersAndResolvesRealServicesWithValidation()
    {
        var database = Mock.Of<IDatabase>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(redis.Object);
        services.AddAdminRedisServices(redisConfigured: true);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.IsType<RedisErrorStore>(provider.GetRequiredService<IRedisErrorStore>());
        Assert.IsType<ProviderErrorTrackingService>(provider.GetRequiredService<IProviderErrorTrackingService>());
        Assert.IsType<RedisVirtualKeyRateLimitService>(provider.GetRequiredService<IVirtualKeyRateLimitService>());
        Assert.All(services.Where(descriptor => descriptor.ServiceType == typeof(IRedisErrorStore) ||
            descriptor.ServiceType == typeof(IProviderErrorTrackingService) ||
            descriptor.ServiceType == typeof(IVirtualKeyRateLimitService)),
            descriptor => Assert.NotNull(descriptor.ImplementationType));
    }

    [Fact]
    public async Task RateLimitUsage_WithoutRedis_PreservesCeilingsAndUnavailableFlag()
    {
        var keys = new Mock<IVirtualKeyRepository>();
        keys.Setup(value => value.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKey { Id = 1, VirtualKeyGroupId = 2, RateLimitRpm = 20 });
        var groups = new Mock<IVirtualKeyGroupRepository>();
        groups.Setup(value => value.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyGroup { Id = 2, RateLimitRpm = 100 });
        using var host = AdminEndpointTestHost.Create(services =>
        {
            services.AddAdminRedisServices(redisConfigured: false);
            services.AddHttpContextAccessor();
            services.AddScoped(_ => Mock.Of<IAdminVirtualKeyService>());
            services.AddScoped(_ => keys.Object);
            services.AddScoped(_ => groups.Object);
            services.AddScoped<VirtualKeysEndpoints>();
        }, endpoints => VirtualKeysEndpoints.MapVirtualKeysEndpoints(endpoints), validateServiceProvider: true);

        using var response = await host.Client.GetAsync("/v1/admin/virtual-keys/1/rate-limit-usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var usage = await response.Content.ReadFromJsonAsync<VirtualKeyRateLimitUsageDto>();
        Assert.True(usage.Unavailable);
        Assert.Equal(20, usage.RateLimitRpm);
        Assert.Equal(100, usage.GroupRateLimitRpm);
        Assert.Null(usage.GroupRequestsThisMinute);
    }
}
