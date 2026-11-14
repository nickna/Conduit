using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Functions.Services;

public class FunctionCostCalculationServiceTieredTests
{
    public static TheoryData<int, decimal> OmittedMinimumCostCases => new()
    {
        { 100, 1.00m },
        { 101, 1.008m },
        { 1000, 8.20m },
        { 1001, 8.205m }
    };

    [Theory]
    [MemberData(nameof(OmittedMinimumCostCases))]
    public async Task CalculateCostAsync_OmittedMinThresholds_InferContiguousRanges(
        int resultCount,
        decimal expectedCost)
    {
        var service = CreateService("""
            {
              "tiers": [
                { "maxThreshold": 100, "costPerUnit": 0.01 },
                { "maxThreshold": 1000, "costPerUnit": 0.008 },
                { "costPerUnit": 0.005 }
              ]
            }
            """);

        var cost = await service.CalculateCostAsync(42, new FunctionExecutionUsage { ResultCount = resultCount });

        Assert.Equal(expectedCost, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_ZeroBasedFirstTier_DoesNotBillAnExtraUnit()
    {
        var service = CreateService("""
            {
              "tiers": [
                { "minThreshold": 0, "maxThreshold": 100, "costPerUnit": 0.01 },
                { "minThreshold": 101, "costPerUnit": 0.008 }
              ]
            }
            """);

        var cost = await service.CalculateCostAsync(42, new FunctionExecutionUsage { ResultCount = 101 });

        Assert.Equal(1.008m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_OmittedMinAfterExplicitTier_ContinuesFromPreviousMaximum()
    {
        var service = CreateService("""
            {
              "tiers": [
                { "minThreshold": 1, "maxThreshold": 100, "costPerUnit": 0.01 },
                { "maxThreshold": 200, "costPerUnit": 0.008 }
              ]
            }
            """);

        var cost = await service.CalculateCostAsync(42, new FunctionExecutionUsage { ResultCount = 150 });

        Assert.Equal(1.40m, cost);
    }

    [Fact]
    public async Task CalculateCostAsync_ExplicitGap_DoesNotBillGapAtNextTierRate()
    {
        var service = CreateService("""
            {
              "tiers": [
                { "minThreshold": 1, "maxThreshold": 100, "costPerUnit": 0.01 },
                { "minThreshold": 201, "costPerUnit": 0.005 }
              ]
            }
            """);

        var cost = await service.CalculateCostAsync(42, new FunctionExecutionUsage { ResultCount = 250 });

        Assert.Equal(1.25m, cost);
    }

    private static FunctionCostCalculationService CreateService(string tieredPricing)
    {
        var functionCost = new FunctionCost
        {
            CostName = "Tiered pricing",
            PricingModel = FunctionPricingModel.Tiered,
            TieredPricing = tieredPricing
        };
        var costService = new Mock<IFunctionCostService>();
        costService
            .Setup(service => service.GetCostForConfigurationAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(functionCost);

        return new FunctionCostCalculationService(
            costService.Object,
            Mock.Of<ILogger<FunctionCostCalculationService>>());
    }
}
