using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Tests.Helpers;
using ConduitLLM.Tests.TestInfrastructure;
using ConduitLLM.Tests.Core.Caching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

/// <summary>
/// Tests for #1238: discovery projects the operator-configured ModelCost so
/// virtual-key clients can preview what a request will cost.
/// </summary>
public sealed class DiscoveryEndpointsPricingTests : IDisposable
{
    private const string Key = "condt_pricing";
    private const string Alias = "priced-model";

    private readonly SqliteTestDatabase _database = new();
    private readonly Mock<IDiscoveryCacheService> _cache = new();

    public DiscoveryEndpointsPricingTests()
    {
        _cache
            .Setup(cache => cache.GetDiscoveryResultsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscoveryModelsResult?)null);
        _cache.Setup(cache => cache.GetOrLoadAsync(It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<DiscoveryModelsResult>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string key, Func<CancellationToken, Task<DiscoveryModelsResult>> load, CancellationToken token) =>
            {
                var result = await load(token);
                await _cache.Object.SetDiscoveryResultsAsync(key, result, token);
                return result;
            });
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task GetModels_ActiveModelCost_ProjectsStandardTokenRates()
    {
        SeedMapping(new ModelCost
        {
            CostName = "priced-model standard",
            PricingModel = PricingModel.Standard,
            InputCostPerMillionTokens = 2.5m,
            OutputCostPerMillionTokens = 10.0m,
            CachedInputCostPerMillionTokens = 0.25m,
            IsActive = true,
            EffectiveDate = DateTime.UtcNow.AddDays(-1)
        });

        var model = await GetSingleModelAsync(CreateEndpoints());

        var pricing = model.GetProperty("pricing");
        Assert.Equal("standard", pricing.GetProperty("pricing_model").GetString());
        Assert.Equal(2.5m, pricing.GetProperty("input_cost_per_million_tokens").GetDecimal());
        Assert.Equal(10.0m, pricing.GetProperty("output_cost_per_million_tokens").GetDecimal());
        Assert.Equal(0.25m, pricing.GetProperty("cached_input_cost_per_million_tokens").GetDecimal());
        Assert.Equal("USD", pricing.GetProperty("currency").GetString());
        // Unconfigured optional rates are omitted, not rendered as zero.
        Assert.False(pricing.TryGetProperty("embedding_cost_per_million_tokens", out _));
    }

    [Fact]
    public async Task GetModels_NonStandardPricingModel_ProjectsDiscriminator()
    {
        SeedMapping(new ModelCost
        {
            CostName = "priced-model per-video",
            PricingModel = PricingModel.PerVideo,
            IsActive = true,
            EffectiveDate = DateTime.UtcNow.AddDays(-1)
        });

        var model = await GetSingleModelAsync(CreateEndpoints());

        Assert.Equal(
            "pervideo",
            model.GetProperty("pricing").GetProperty("pricing_model").GetString());
    }

    [Fact]
    public async Task GetModels_NoModelCost_OmitsPricing()
    {
        SeedMapping(cost: null);

        var model = await GetSingleModelAsync(CreateEndpoints());

        Assert.False(model.TryGetProperty("pricing", out _));
    }

    [Theory]
    [InlineData(false, -1, null)] // disabled cost
    [InlineData(true, 1, null)]   // not yet effective
    [InlineData(true, -2, -1)]    // expired
    public async Task GetModels_CostOutsideActiveWindow_OmitsPricing(
        bool isActive, int effectiveOffsetDays, int? expiryOffsetDays)
    {
        SeedMapping(new ModelCost
        {
            CostName = "priced-model inactive",
            InputCostPerMillionTokens = 2.5m,
            OutputCostPerMillionTokens = 10.0m,
            IsActive = isActive,
            EffectiveDate = DateTime.UtcNow.AddDays(effectiveOffsetDays),
            ExpiryDate = expiryOffsetDays.HasValue
                ? DateTime.UtcNow.AddDays(expiryOffsetDays.Value)
                : null
        });

        var model = await GetSingleModelAsync(CreateEndpoints());

        Assert.False(model.TryGetProperty("pricing", out _));
    }

    [Fact]
    public async Task GetModels_ExposePricingDisabled_OmitsPricingAndUsesUnpricedCacheKey()
    {
        SeedMapping(new ModelCost
        {
            CostName = "priced-model hidden",
            InputCostPerMillionTokens = 2.5m,
            OutputCostPerMillionTokens = 10.0m,
            IsActive = true,
            EffectiveDate = DateTime.UtcNow.AddDays(-1)
        });

        var model = await GetSingleModelAsync(CreateEndpoints(exposePricing: false));

        Assert.False(model.TryGetProperty("pricing", out _));
        _cache.Verify(cache => cache.SetDiscoveryResultsAsync(
            "all", It.IsAny<DiscoveryModelsResult>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task GetModels_ExposePricingEnabled_CachesUnderPricingVariantKey()
    {
        SeedMapping(cost: null);

        await GetSingleModelAsync(CreateEndpoints());

        _cache.Verify(cache => cache.SetDiscoveryResultsAsync(
            "all:with_pricing", It.IsAny<DiscoveryModelsResult>(), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("speech_to_text")]
    [InlineData("audio_transcription")]
    [InlineData("text_to_speech")]
    [InlineData("rerank")]
    public async Task GetModels_SharedProjectorSupportsExtendedCapabilities(string capability)
    {
        SeedMapping(cost: null);

        await GetSingleModelAsync(CreateEndpoints(), capability);
    }

    [Fact]
    public async Task CacheWarmer_UsesPricedKeyAndTheSameWireProjection()
    {
        SeedMapping(new ModelCost
        {
            CostName = "warmed price",
            InputCostPerMillionTokens = 1m,
            OutputCostPerMillionTokens = 2m,
            IsActive = true,
            EffectiveDate = DateTime.UtcNow.AddDays(-1)
        });
        var warmer = new DiscoveryCacheWarmingService(
            Mock.Of<IServiceProvider>(),
            _cache.Object,
            Options.Create(new DiscoveryCacheOptions { ExposePricing = true }),
            GatewayJsonOptions.Create(),
            Mock.Of<ILogger<DiscoveryCacheWarmingService>>());

        await warmer.WarmCacheForCapability(
            _database.CreateDbContextFactory(),
            "speech_to_text",
            CancellationToken.None);

        _cache.Verify(cache => cache.SetDiscoveryResultsAsync(
            "capability:speech_to_text:with_pricing",
            It.Is<DiscoveryModelsResult>(result => HasSinglePricedModel(result)),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task FusionCache_WarmedAndColdPayloadsMatchAndPricingVisibilityRemainsSeparate()
    {
        SeedMapping(new ModelCost
        {
            CostName = "fusion priced model", InputCostPerMillionTokens = 0.25m,
            IsActive = true, EffectiveDate = DateTime.UtcNow.AddDays(-1)
        });
        using var host = FusionDiscoveryCacheTests.Host();
        var cache = host.GetRequiredService<IDiscoveryCacheService>();
        var priced = CreateEndpoints(cache: cache);
        var cold = await RenderAsync(await priced.GetModels("speech_to_text"));
        var warm = await RenderAsync(await priced.GetModels("speech_to_text"));
        Assert.Equal(cold, warm);
        var warmer = new DiscoveryCacheWarmingService(Mock.Of<IServiceProvider>(), cache,
            Options.Create(new DiscoveryCacheOptions()), GatewayJsonOptions.Create(),
            Mock.Of<ILogger<DiscoveryCacheWarmingService>>());
        await cache.InvalidateAllDiscoveryAsync();
        await warmer.WarmCacheForCapability(_database.CreateDbContextFactory(), "speech_to_text", CancellationToken.None);
        Assert.Equal(cold, await RenderAsync(await priced.GetModels("speech_to_text")));
        var unpriced = await GetSingleModelAsync(CreateEndpoints(exposePricing: false, cache: cache), "speech_to_text");
        Assert.False(unpriced.TryGetProperty("pricing", out _));
        Assert.True((await GetSingleModelAsync(priced, "speech_to_text")).TryGetProperty("pricing", out _));
    }

    private void SeedMapping(ModelCost? cost)
    {
        _database.Seed(context =>
        {
            var model = ModelTestHelper.CreateCompleteTestModel(Alias);
            model.SupportsSpeechToText = true;
            model.SupportsTextToSpeech = true;
            model.SupportsRerank = true;
            var provider = new Provider
            {
                ProviderName = "OpenAI primary",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };
            context.Models.Add(model);
            context.Providers.Add(provider);
            context.SaveChanges();

            var association = new ModelProviderTypeAssociation
            {
                ModelId = model.Id,
                Identifier = $"openai/{Alias}",
                Provider = ProviderType.OpenAI,
                IsEnabled = true,
                IsPrimary = true,
                ModelCost = cost
            };
            context.ModelProviderTypeAssociations.Add(association);
            context.SaveChanges();

            context.ModelProviderMappings.Add(new ModelProviderMapping
            {
                ModelAlias = Alias,
                ProviderId = provider.Id,
                ProviderModelId = $"openai/{Alias}",
                ModelProviderTypeAssociationId = association.Id,
                IsEnabled = true
            });
            context.SaveChanges();
        });
    }

    [Fact]
    public async Task WarmedDiscoveryRefreshesAtEffectiveAndExpiryTimesWithoutMutation()
    {
        var clock = new FusionPricingCacheTests.Clock();
        SeedMapping(new ModelCost { CostName = "scheduled", IsActive = true,
            InputCostPerMillionTokens = 0.25m, EffectiveDate = clock.Now.AddSeconds(10).UtcDateTime,
            ExpiryDate = clock.Now.AddSeconds(20).UtcDateTime });
        using var host = FusionDiscoveryCacheTests.Host(clock: clock);
        var cache = host.GetRequiredService<IDiscoveryCacheService>();
        var endpoints = CreateEndpoints(cache: cache, clock: clock);
        var warmer = new DiscoveryCacheWarmingService(Mock.Of<IServiceProvider>(), cache,
            Options.Create(new DiscoveryCacheOptions()), GatewayJsonOptions.Create(),
            Mock.Of<ILogger<DiscoveryCacheWarmingService>>(), clock);
        await warmer.WarmCacheForCapability(_database.CreateDbContextFactory(), null, CancellationToken.None);
        Assert.False((await GetSingleModelAsync(endpoints)).TryGetProperty("pricing", out _));
        clock.Now = clock.Now.AddSeconds(10);
        Assert.True((await GetSingleModelAsync(endpoints)).TryGetProperty("pricing", out _));
        clock.Now = clock.Now.AddSeconds(10);
        Assert.False((await GetSingleModelAsync(endpoints)).TryGetProperty("pricing", out _));
        Assert.False((await GetSingleModelAsync(endpoints)).TryGetProperty("pricing_refresh_at", out _));
        Assert.False((await GetSingleModelAsync(CreateEndpoints(exposePricing: false, cache: cache, clock: clock))).TryGetProperty("pricing", out _));
    }

    private DiscoveryEndpoints CreateEndpoints(bool exposePricing = true, IDiscoveryCacheService? cache = null, TimeProvider? clock = null)
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
            cache ?? _cache.Object,
            GatewayJsonOptions.Create(),
            Options.Create(new DiscoveryCacheOptions { ExposePricing = exposePricing }),
            Mock.Of<IHttpContextAccessor>(accessor => accessor.HttpContext == httpContext),
            Mock.Of<ILogger<DiscoveryEndpoints>>(), clock);
    }

    private static async Task<JsonElement> GetSingleModelAsync(
        DiscoveryEndpoints endpoints,
        string? capability = null)
    {
        var json = await RenderAsync(await endpoints.GetModels(capability));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        return document.RootElement.GetProperty("data")[0].Clone();
    }

    private static bool HasSinglePricedModel(DiscoveryModelsResult result) =>
        result.Count == 1 &&
        result.Data[0].TryGetProperty("pricing", out _);

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
}
