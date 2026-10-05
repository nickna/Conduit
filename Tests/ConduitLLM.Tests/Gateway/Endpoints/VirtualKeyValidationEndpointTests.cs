using System.Security.Claims;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class VirtualKeyValidationEndpointTests
{
    [Theory]
    [InlineData(VirtualKeyValidationFailureCodes.KeyNotFound, 401)]
    [InlineData(VirtualKeyValidationFailureCodes.KeyDisabled, 401)]
    [InlineData(VirtualKeyValidationFailureCodes.KeyExpired, 401)]
    [InlineData(VirtualKeyValidationFailureCodes.InsufficientBalance, 402)]
    [InlineData(VirtualKeyValidationFailureCodes.ModelNotAllowed, 403)]
    public async Task RequireBalanceFilter_MapsTypedValidationFailure(
        string failureCode,
        int statusCode)
    {
        const string key = "condt_endpoint_test";
        var httpContext = CreateAuthenticatedContext(key);
        httpContext.Request.RouteValues["model"] = "restricted-model";

        var virtualKeyService = new Mock<IVirtualKeyService>();
        virtualKeyService
            .Setup(service => service.ValidateVirtualKeyAsync(key, "restricted-model"))
            .ReturnsAsync(VirtualKeyValidationOutcome.Failure(
                failureCode,
                statusCode,
                "Validation failed."));

        var filter = new RequireBalanceEndpointFilter(
            virtualKeyService.Object,
            Mock.Of<ILogger<RequireBalanceEndpointFilter>>());
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        var httpResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(statusCode, httpResult.StatusCode);
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var error = Assert.IsType<OpenAIErrorResponse>(valueResult.Value);
        Assert.Equal(failureCode, error.Error.Code);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task RequireBalanceFilter_WithValidKey_ContinuesAndStoresValidatedKey()
    {
        const string key = "condt_valid";
        var httpContext = CreateAuthenticatedContext(key);
        var keyEntity = new VirtualKey { Id = 7, IsEnabled = true };
        var virtualKeyService = new Mock<IVirtualKeyService>();
        virtualKeyService
            .Setup(service => service.ValidateVirtualKeyAsync(key, null))
            .ReturnsAsync(VirtualKeyValidationOutcome.Success(keyEntity));

        var filter = new RequireBalanceEndpointFilter(
            virtualKeyService.Object,
            Mock.Of<ILogger<RequireBalanceEndpointFilter>>());
        var expected = new object();

        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ => ValueTask.FromResult<object?>(expected));

        Assert.Same(expected, result);
        Assert.Same(keyEntity, httpContext.Items["ValidatedVirtualKey"]);
    }

    [Fact]
    public async Task Discovery_WithValidKey_UsesAuthenticationOnlyValidation()
    {
        const string key = "condt_discovery";
        var httpContext = CreateAuthenticatedContext(key);
        var virtualKeyService = new Mock<IVirtualKeyService>();
        virtualKeyService
            .Setup(service => service.ValidateVirtualKeyForAuthenticationAsync(key, null))
            .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
            {
                Id = 11,
                IsEnabled = true
            }));
        var discoveryCache = new Mock<IDiscoveryCacheService>();
        discoveryCache
            .Setup(cache => cache.GetOrLoadAsync(It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<DiscoveryModelsResult>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveryModelsResult());

        var endpoints = new DiscoveryEndpoints(
            Mock.Of<IDbContextFactory<ConduitDbContext>>(),
            Mock.Of<IModelCapabilityService>(),
            virtualKeyService.Object,
            discoveryCache.Object,
            GatewayJsonOptions.Create(),
            Options.Create(new DiscoveryCacheOptions()),
            Mock.Of<IHttpContextAccessor>(accessor => accessor.HttpContext == httpContext),
            Mock.Of<ILogger<DiscoveryEndpoints>>());

        var result = await endpoints.GetModels();

        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        virtualKeyService.Verify(
            service => service.ValidateVirtualKeyForAuthenticationAsync(key, null),
            Times.Once);
        virtualKeyService.Verify(
            service => service.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    private static DefaultHttpContext CreateAuthenticatedContext(string key)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("VirtualKey", key) },
                "TestAuthentication"))
        };
        context.Items["VirtualKey"] = key;
        return context;
    }

    [Fact]
    public async Task Discovery_RevokedKeyCannotReadCachedPayloadOrInvokeLoader()
    {
        var httpContext = CreateAuthenticatedContext("revoked");
        var keys = new Mock<IVirtualKeyService>();
        keys.Setup(service => service.ValidateVirtualKeyForAuthenticationAsync("revoked", null))
            .ReturnsAsync(VirtualKeyValidationOutcome.Failure(VirtualKeyValidationFailureCodes.KeyDisabled, 401, "Disabled"));
        var cache = new Mock<IDiscoveryCacheService>(MockBehavior.Strict);
        var factory = new Mock<IDbContextFactory<ConduitDbContext>>(MockBehavior.Strict);
        var endpoints = new DiscoveryEndpoints(factory.Object, Mock.Of<IModelCapabilityService>(), keys.Object,
            cache.Object, GatewayJsonOptions.Create(), Options.Create(new DiscoveryCacheOptions()),
            Mock.Of<IHttpContextAccessor>(accessor => accessor.HttpContext == httpContext),
            Mock.Of<ILogger<DiscoveryEndpoints>>());
        var result = await endpoints.GetModels();
        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        cache.VerifyNoOtherCalls();
        factory.VerifyNoOtherCalls();
    }

    private sealed class TestEndpointFilterInvocationContext : EndpointFilterInvocationContext
    {
        public TestEndpointFilterInvocationContext(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public override HttpContext HttpContext { get; }
        public override IList<object?> Arguments { get; } = new List<object?>();

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }
}
