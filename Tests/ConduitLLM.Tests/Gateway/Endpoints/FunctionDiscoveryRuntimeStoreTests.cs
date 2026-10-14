using System.Security.Claims;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public sealed class FunctionDiscoveryRuntimeStoreTests
{
    [Fact]
    public async Task RegisteredHandlerUsesInjectedStoreAndPreservesCacheWireShapesWithoutEf()
    {
        using var fixture = new Fixture();
        var miss = await fixture.RenderAsync(await fixture.Handler.GetFunctions());
        var hit = await fixture.RenderAsync(await fixture.Handler.GetFunctions());
        Assert.Equal(miss, hit);
        Assert.Equal(1, fixture.Store.ListCalls);
        Assert.Contains("functions_discovery_v4_all_all", fixture.Cache.Keys);
        using (var catalog = JsonDocument.Parse(hit))
        {
            Assert.Equal(JsonValueKind.Object, catalog.RootElement.ValueKind);
            Assert.Equal(3, catalog.RootElement.GetProperty("count").GetInt32());
        }
        miss = await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(1));
        hit = await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(1));
        Assert.Equal(miss, hit);
        Assert.Equal(1, fixture.Store.IdCalls);
        Assert.Contains("function_parameters_v4_1", fixture.Cache.Keys);
        using var parameters = JsonDocument.Parse(hit);
        Assert.Equal(1, parameters.RootElement.GetProperty("function_id").GetInt32());
        Assert.Equal(JsonValueKind.Object, parameters.RootElement.GetProperty("parameter_schema").ValueKind);
        Assert.Equal("雪", parameters.RootElement.GetProperty("example_request").GetProperty("query").GetString());
        fixture.Factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Auth.Verify(service => service.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("sEaRcH", "eXa", FunctionPurpose.Search, FunctionProviderType.Exa)]
    [InlineData("1", "1", FunctionPurpose.Search, FunctionProviderType.Exa)]
    [InlineData("-9", "197", (FunctionPurpose)(-9), (FunctionProviderType)197)]
    [InlineData("999", "999", (FunctionPurpose)999, (FunctionProviderType)999)]
    [InlineData("not a purpose", "not a provider", null, null)]
    public async Task FiltersPreserveNamedNumericAndIgnoredInvalidTextSemantics(
        string purpose, string provider, FunctionPurpose? expectedPurpose, FunctionProviderType? expectedProvider)
    {
        using var fixture = new Fixture();
        await fixture.Handler.GetFunctions(purpose, provider);
        Assert.Equal(expectedPurpose, fixture.Store.LastPurpose);
        Assert.Equal(expectedProvider, fixture.Store.LastProvider);
        Assert.Contains($"functions_discovery_v4_{purpose ?? "all"}_{provider ?? "all"}", fixture.Cache.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationRejectsBeforeCacheOrStoreEvenAfterAnEntryWasCached(bool missingClaim)
    {
        using var fixture = new Fixture();
        await fixture.Handler.GetFunctions();
        var reads = fixture.Cache.Reads;
        if (missingClaim) fixture.Context.User = new ClaimsPrincipal();
        else fixture.Auth.Setup(service => service.ValidateVirtualKeyForAuthenticationAsync("fixture", null))
            .ReturnsAsync(VirtualKeyValidationOutcome.Failure(VirtualKeyValidationFailureCodes.KeyDisabled, 401, "disabled"));
        await fixture.RenderAsync(await fixture.Handler.GetFunctions(), 401);
        await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(1), 401);
        Assert.Equal(reads, fixture.Cache.Reads);
        Assert.Equal(1, fixture.Store.ListCalls);
        Assert.Equal(0, fixture.Store.IdCalls);
    }

    [Fact]
    public async Task MissingDisabledAndNonObjectSchemasPreserveExistingResponses()
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(3), 404);
        await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(999), 404);
        using var array = JsonDocument.Parse(await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(4)));
        Assert.Equal("雪", array.RootElement.GetProperty("parameter_schema").GetProperty("value")[0].GetString());
        using var unknown = JsonDocument.Parse(await fixture.RenderAsync(await fixture.Handler.GetFunctionParameters(2)));
        Assert.Equal("197", unknown.RootElement.GetProperty("provider_type").GetString());
        Assert.Equal("-9", unknown.RootElement.GetProperty("purpose").GetString());
        Assert.Equal(0, unknown.RootElement.GetProperty("parameter_schema").EnumerateObject().Count());
    }

    [Fact]
    public async Task RequestAbortFlowsThroughTheCacheLoaderToBothStoreOperations()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        fixture.Context.RequestAborted = cancelled.Token;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Handler.GetFunctions());
        Assert.Equal(cancelled.Token, fixture.Store.LastToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Handler.GetFunctionParameters(1));
        Assert.Equal(cancelled.Token, fixture.Store.LastToken);
        fixture.Factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ExistingPublicConstructorRemainsAvailable()
    {
        Assert.NotNull(typeof(DiscoveryEndpoints).GetConstructor([
            typeof(IDbContextFactory<ConduitDbContext>), typeof(IModelCapabilityService), typeof(IVirtualKeyRuntimeService),
            typeof(IDiscoveryCacheService), typeof(JsonSerializerOptions), typeof(IOptions<DiscoveryCacheOptions>),
            typeof(IHttpContextAccessor), typeof(Microsoft.Extensions.Logging.ILogger<DiscoveryEndpoints>), typeof(TimeProvider)]));
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Mock<IDbContextFactory<ConduitDbContext>> Factory = new(MockBehavior.Strict);
        public readonly Mock<IVirtualKeyRuntimeService> Auth = new(MockBehavior.Strict);
        public readonly Store Store = new();
        public readonly Cache Cache = new();
        public readonly DefaultHttpContext Context = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("VirtualKey", "fixture")], "fixture"))
        };
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        public DiscoveryEndpoints Handler { get; }

        public Fixture()
        {
            Auth.Setup(service => service.ValidateVirtualKeyForAuthenticationAsync("fixture", null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
                { Id = 1, IsEnabled = true, VirtualKeyGroup = new VirtualKeyGroup { Balance = 0 } }));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRepositories();
#if !CONDUIT_NATIVE_AOT
            var reference = services.Last(service => service.ServiceType == typeof(IFunctionDiscoveryRuntimeStore));
            Assert.Equal(typeof(EfFunctionDiscoveryRuntimeStore), reference.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, reference.Lifetime);
#endif
            services.Replace(ServiceDescriptor.Singleton<IFunctionDiscoveryRuntimeStore>(Store));
            services.AddSingleton(Factory.Object);
            services.AddSingleton(Auth.Object);
            services.AddSingleton(Mock.Of<IModelCapabilityService>());
            services.AddSingleton<IDiscoveryCacheService>(Cache);
            services.AddSingleton(GatewayJsonOptions.Create());
            services.AddSingleton(Options.Create(new DiscoveryCacheOptions()));
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = Context });
            services.ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions));
            services.AddGatewayEndpointHandlers();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _scope = _provider.CreateScope();
            Handler = _scope.ServiceProvider.GetRequiredService<DiscoveryEndpoints>();
        }

        public async Task<string> RenderAsync(IResult result, int status = 200)
        {
            var context = new DefaultHttpContext { RequestServices = _scope.ServiceProvider };
            using var body = new MemoryStream();
            context.Response.Body = body;
            await result.ExecuteAsync(context);
            Assert.Equal(status, context.Response.StatusCode);
            return Encoding.UTF8.GetString(body.ToArray());
        }

        public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
    }

    private sealed class Store : IFunctionDiscoveryRuntimeStore
    {
        private readonly FunctionConfiguration[] _rows =
        [
            new() { Id = 1, ConfigurationName = "Search 雪", ProviderType = FunctionProviderType.Exa,
                Purpose = FunctionPurpose.Search, ParameterSchema = """{"example":{"query":"雪"},"required":["query"]}""", IsEnabled = true },
            new() { Id = 2, ConfigurationName = "Unknown", ProviderType = (FunctionProviderType)197,
                Purpose = (FunctionPurpose)(-9), IsEnabled = true },
            new() { Id = 3, ConfigurationName = "Disabled", IsEnabled = false },
            new() { Id = 4, ConfigurationName = "Array schema", ParameterSchema = """["雪",1]""", IsEnabled = true }
        ];
        public int ListCalls, IdCalls;
        public FunctionPurpose? LastPurpose;
        public FunctionProviderType? LastProvider;
        public CancellationToken LastToken;

        public Task<IReadOnlyList<FunctionConfiguration>> ListEnabledAsync(FunctionPurpose? purpose = null,
            FunctionProviderType? providerType = null, CancellationToken cancellationToken = default)
        {
            ListCalls++; LastPurpose = purpose; LastProvider = providerType; LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<FunctionConfiguration>>(_rows.Where(row => row.IsEnabled &&
                (!purpose.HasValue || row.Purpose == purpose) && (!providerType.HasValue || row.ProviderType == providerType)).ToArray());
        }
        public Task<FunctionConfiguration> GetEnabledByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            IdCalls++; LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_rows.SingleOrDefault(row => row.Id == id && row.IsEnabled));
        }
    }

    private sealed class Cache : IDiscoveryCacheService
    {
        private readonly Dictionary<string, DiscoveryModelsResult> _entries = new();
        public int Reads;
        public IEnumerable<string> Keys => _entries.Keys;
        public async Task<DiscoveryModelsResult> GetOrLoadAsync(string key, Func<CancellationToken, Task<DiscoveryModelsResult>> load,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            if (_entries.TryGetValue(key, out var cached)) return cached;
            var value = await load(cancellationToken);
            _entries.Add(key, value);
            return value;
        }
        public Task<DiscoveryModelsResult> GetDiscoveryResultsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.GetValueOrDefault(key));
        public Task SetDiscoveryResultsAsync(string key, DiscoveryModelsResult value, CancellationToken cancellationToken = default)
        { _entries[key] = value; return Task.CompletedTask; }
        public Task InvalidateAllDiscoveryAsync(CancellationToken cancellationToken = default) { _entries.Clear(); return Task.CompletedTask; }
        public Task InvalidatePatternAsync(string pattern, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CacheStats> GetStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CacheStats());
    }
}
