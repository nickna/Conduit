using System.Text.Json;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Services;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Functions.Services;

public class FunctionCostCalculationServiceHybridTests
{
    [Fact]
    public async Task EstimateCostAsync_MissingCostMapping_FailsClosed()
    {
        var costService = new Mock<IFunctionCostService>();
        costService
            .Setup(service => service.GetCostForConfigurationAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FunctionCost?)null);
        var service = new FunctionCostCalculationService(
            costService.Object,
            Mock.Of<ILogger<FunctionCostCalculationService>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EstimateCostAsync(42, new Dictionary<string, object> { ["query"] = "test" }));
    }

    [Fact]
    public async Task CalculateCostAsync_MissingCostMappingWithoutProviderCost_FailsClosed()
    {
        var costService = new Mock<IFunctionCostService>();
        costService
            .Setup(service => service.GetCostForConfigurationAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FunctionCost?)null);
        var service = new FunctionCostCalculationService(
            costService.Object,
            Mock.Of<ILogger<FunctionCostCalculationService>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CalculateCostAsync(42, new FunctionExecutionUsage()));
    }

    [Fact]
    public async Task CalculateCostAsync_ExaContents_BillsRetrievalAndExtractionWithoutSearch()
    {
        var service = CreateService(CreateExaCost());
        var usage = new FunctionExecutionUsage
        {
            ResultCount = 10,
            TextPagesExtracted = 10,
            Metadata = new Dictionary<string, object> { ["operation"] = "contents" }
        };

        var cost = await service.CalculateCostAsync(42, usage);

        Assert.Equal(0.020m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_ExaSearchWithNoResults_BillsRequestFee()
    {
        var service = CreateService(CreateExaCost());
        var usage = new FunctionExecutionUsage
        {
            ResultCount = 0,
            SearchType = "keyword"
        };

        var cost = await service.CalculateCostAsync(42, usage);

        Assert.Equal(0.0025m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_ProviderReportedCost_OverridesConfiguredCost()
    {
        var logger = new Mock<ILogger<FunctionCostCalculationService>>();
        var service = CreateService(CreateExaCost(), logger.Object);
        var usage = new FunctionExecutionUsage
        {
            ResultCount = 10,
            SearchType = "keyword",
            ProviderReportedCost = 0.004m
        };

        var cost = await service.CalculateCostAsync(42, usage);

        Assert.Equal(0.004m, cost);
        logger.VerifyLog(LogLevel.Warning, "Function cost drift detected", Times.Once());
    }

    [Fact]
    public async Task CalculateCostAsync_ZeroProviderReportedCost_OverridesConfiguredCost()
    {
        var service = CreateService(CreateExaCost());
        var usage = new FunctionExecutionUsage
        {
            ResultCount = 10,
            SearchType = "keyword",
            ProviderReportedCost = 0m
        };

        var cost = await service.CalculateCostAsync(42, usage);

        Assert.Equal(0m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_NegativeProviderReportedCost_IsIgnored()
    {
        var service = CreateService(CreateExaCost());
        var usage = new FunctionExecutionUsage
        {
            ResultCount = 10,
            SearchType = "keyword",
            ProviderReportedCost = -1m
        };

        var cost = await service.CalculateCostAsync(42, usage);

        Assert.Equal(0.0025m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_PerplexityConfig_BillsBaseAndTypedTokens()
    {
        var service = CreateService(CreatePerplexityCost());
        var usage = new FunctionExecutionUsage
        {
            InputTokensConsumed = 1_000,
            OutputTokensConsumed = 500
        };

        var cost = await service.CalculateCostAsync(42, usage);

        Assert.Equal(0.006995m, cost);
    }

    [Fact]
    public async Task EstimateCostAsync_PerplexityConfig_ReservesBaseAndMaximumOutputCost()
    {
        var service = CreateService(CreatePerplexityCost());

        var cost = await service.EstimateCostAsync(42, new Dictionary<string, object>
        {
            ["messages"] = new object(),
            ["maxTokens"] = 4_096
        });

        Assert.Equal(0.01044768m, cost);
    }

    [Fact]
    public async Task EstimateCostAsync_JsonElementParameters_AreConvertedForHybridProviders()
    {
        var perplexityService = CreateService(CreatePerplexityCost());
        var perplexityParameters = DeserializeParameters("""{"messages":[],"max_tokens":4096}""");

        var perplexityCost = await perplexityService.EstimateCostAsync(42, perplexityParameters);

        Assert.Equal(0.01044768m, perplexityCost);

        var exaService = CreateService(CreateExaCost());
        var exaParameters = DeserializeParameters("""{"numResults":30,"text":true}""");

        var exaCost = await exaService.EstimateCostAsync(42, exaParameters);

        Assert.Equal(0.055m, exaCost);
    }

    [Fact]
    public async Task EstimateCostAsync_TavilySnakeCaseJsonElements_IncludeAutoParametersSurcharge()
    {
        var service = CreateService(CreateTavilyCost());
        var parameters = DeserializeParameters("""{"query":"test","search_depth":"advanced","auto_parameters":true}""");

        var cost = await service.EstimateCostAsync(42, parameters);

        Assert.Equal(0.032m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_MalformedTavilyConfig_FailsClosed()
    {
        var malformedCost = new FunctionCost
        {
            CostName = "Malformed Tavily",
            ProviderType = FunctionProviderType.Tavily,
            PricingModel = FunctionPricingModel.Hybrid,
            PricingConfiguration = "{\"baseRequestCost\":0.005}"
        };
        var service = CreateService(malformedCost);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CalculateCostAsync(42, new FunctionExecutionUsage()));

        Assert.Contains("Invalid Tavily hybrid pricing configuration", exception.Message);
    }

    [Fact]
    public async Task EstimateCostAsync_MalformedPerplexityConfig_FailsClosed()
    {
        var malformedCost = CreatePerplexityCost();
        malformedCost.PricingConfiguration = "{\"baseRequestCost\":0.005}";
        var service = CreateService(malformedCost);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EstimateCostAsync(42, new Dictionary<string, object> { ["messages"] = new object() }));

        Assert.Contains("Invalid Perplexity hybrid pricing configuration", exception.Message);
    }

    private static FunctionCostCalculationService CreateService(
        FunctionCost cost,
        ILogger<FunctionCostCalculationService>? logger = null)
    {
        var costService = new Mock<IFunctionCostService>();
        costService
            .Setup(service => service.GetCostForConfigurationAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cost);

        return new FunctionCostCalculationService(
            costService.Object,
            logger ?? Mock.Of<ILogger<FunctionCostCalculationService>>());
    }

    private static FunctionCost CreatePerplexityCost() => new()
    {
        CostName = "Perplexity Sonar",
        ProviderType = FunctionProviderType.Perplexity,
        PricingModel = FunctionPricingModel.Hybrid,
        PricingConfiguration = """
            {
              "baseRequestCost": 0.005,
              "inputTokenCostPerMillion": 1.33,
              "outputTokenCostPerMillion": 1.33
            }
            """
    };

    private static FunctionCost CreateExaCost() => new()
    {
        CostName = "Exa Hybrid",
        ProviderType = FunctionProviderType.Exa,
        PricingModel = FunctionPricingModel.Hybrid,
        PricingConfiguration = """
            {
              "searchCosts": {
                "neural": {
                  "tier1": { "maxResults": 25, "cost": 0.005 },
                  "tier2": { "maxResults": null, "cost": 0.025 }
                },
                "keyword": { "cost": 0.0025 },
                "auto": { "fallbackToKeyword": true }
              },
              "contentRetrievalCosts": { "costPer1000Pages": 1.0 },
              "contentExtractionCosts": {
                "text": 0.001,
                "highlights": 0.001,
                "summary": 0.001
              }
            }
            """
    };

    private static FunctionCost CreateTavilyCost() => new()
    {
        CostName = "Tavily Hybrid",
        ProviderType = FunctionProviderType.Tavily,
        PricingModel = FunctionPricingModel.Hybrid,
        PricingConfiguration = """
            {
              "costPerCredit": 0.008,
              "basicSearchCredits": 1,
              "advancedSearchCredits": 2,
              "autoParametersCredits": 2
            }
            """
    };

    private static Dictionary<string, object> DeserializeParameters(string json) =>
        JsonSerializer.Deserialize(json, FunctionsJsonContext.Default.DictionaryStringObject)!;
}
