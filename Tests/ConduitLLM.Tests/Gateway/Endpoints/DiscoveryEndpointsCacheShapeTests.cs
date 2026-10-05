using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

/// <summary>
/// Regression tests for #1149: function discovery responses must not change
/// shape (object vs array, property casing) between cache misses and cache hits.
/// </summary>
public sealed class DiscoveryEndpointsCacheShapeTests : IDisposable
{
    private const string Key = "condt_cache_shape";

    private readonly SqliteTestDatabase _database = new();
    private readonly InMemoryDiscoveryCacheService _cache = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task GetFunctions_CacheHit_ReturnsSameJsonAsCacheMiss()
    {
        SeedConfiguration();
        var endpoints = CreateEndpoints();

        var missJson = await RenderAsync(await endpoints.GetFunctions());
        var hitJson = await RenderAsync(await endpoints.GetFunctions());

        Assert.Equal(1, _cache.Hits);
        Assert.Equal(missJson, hitJson);

        using var document = JsonDocument.Parse(hitJson);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("functions").ValueKind);
    }

    [Fact]
    public async Task GetFunctionParameters_CacheHit_ReturnsSameJsonAsCacheMiss()
    {
        var configurationId = SeedConfiguration(
            parameterSchema: """{"required":["query"],"example":{"query":"test"}}""");
        var endpoints = CreateEndpoints();

        var missJson = await RenderAsync(await endpoints.GetFunctionParameters(configurationId));
        var hitJson = await RenderAsync(await endpoints.GetFunctionParameters(configurationId));

        Assert.Equal(1, _cache.Hits);
        Assert.Equal(missJson, hitJson);

        using var document = JsonDocument.Parse(hitJson);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(configurationId, document.RootElement.GetProperty("function_id").GetInt32());
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("parameter_schema").ValueKind);
        Assert.Equal("test", document.RootElement.GetProperty("example_request").GetProperty("query").GetString());
    }

    private int SeedConfiguration(string? parameterSchema = null)
    {
        var configuration = new FunctionConfiguration
        {
            ConfigurationName = "Test Exa Search",
            ProviderType = FunctionProviderType.Exa,
            Purpose = FunctionPurpose.Search,
            DefaultExecutionMode = ExecutionMode.Synchronous,
            IsEnabled = true,
            TimeoutSeconds = 30,
            ParameterSchema = parameterSchema
        };

        _database.Seed(context =>
        {
            context.FunctionConfigurations.Add(configuration);
            context.SaveChanges();
        });

        return configuration.Id;
    }

    private DiscoveryEndpoints CreateEndpoints()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("VirtualKey", Key) },
                "TestAuthentication"))
        };

        var virtualKeyService = new Mock<IVirtualKeyService>();
        virtualKeyService
            .Setup(service => service.ValidateVirtualKeyForAuthenticationAsync(Key, null))
            .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
            {
                Id = 1,
                IsEnabled = true
            }));

        return new DiscoveryEndpoints(
            _database.CreateDbContextFactory(),
            Mock.Of<IModelCapabilityService>(),
            virtualKeyService.Object,
            _cache,
            GatewayJsonOptions.Create(),
            Options.Create(new DiscoveryCacheOptions()),
            Mock.Of<IHttpContextAccessor>(accessor => accessor.HttpContext == httpContext),
            Mock.Of<ILogger<DiscoveryEndpoints>>());
    }

    /// <summary>
    /// Executes the result with the Gateway's canonical wire serializer and
    /// returns the raw response body.
    /// </summary>
    private static async Task<string> RenderAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(options =>
            GatewayJsonOptions.Configure(options.SerializerOptions));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        using var body = new MemoryStream();
        httpContext.Response.Body = body;

        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private sealed class InMemoryDiscoveryCacheService : IDiscoveryCacheService
    {
        private readonly Dictionary<string, DiscoveryModelsResult> _entries = new();

        public int Hits { get; private set; }

        public async Task<DiscoveryModelsResult> GetOrLoadAsync(string cacheKey,
            Func<CancellationToken, Task<DiscoveryModelsResult>> load, CancellationToken cancellationToken = default)
        {
            var cached = await GetDiscoveryResultsAsync(cacheKey, cancellationToken);
            if (cached is not null) return cached;
            var value = await load(cancellationToken); await SetDiscoveryResultsAsync(cacheKey, value, cancellationToken);
            return value;
        }

        public Task<DiscoveryModelsResult?> GetDiscoveryResultsAsync(
            string cacheKey, CancellationToken cancellationToken = default)
        {
            if (_entries.TryGetValue(cacheKey, out var result))
            {
                Hits++;
                return Task.FromResult<DiscoveryModelsResult?>(result);
            }

            return Task.FromResult<DiscoveryModelsResult?>(null);
        }

        public Task SetDiscoveryResultsAsync(
            string cacheKey, DiscoveryModelsResult results, CancellationToken cancellationToken = default)
        {
            _entries[cacheKey] = results;
            return Task.CompletedTask;
        }

        public Task InvalidateAllDiscoveryAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task InvalidatePatternAsync(string pattern, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<CacheStats> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CacheStats());
    }
}
