using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Core.Services;

public sealed class UsageBillingCopyTests
{
    [Fact]
    public async Task RulesPricing_EnrichesBillingCopyWithoutChangingProviderParameters()
    {
        var providerUsage = new Usage
        {
            VideoDurationSeconds = 10, VideoResolution = "1920x1080",
            ImageResolution = "1024x1024", ImageQuality = "hd",
            PricingParameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider_hint"] = "retained"
            }
        };
        var config = new PricingRulesConfig
        {
            PricingType = "per_second", UnitField = "VideoDurationSeconds", DefaultRate = 0.01m,
            Rules =
            [
                new PricingRule
                {
                    Priority = 1, Rate = 0.06m,
                    Conditions = new Dictionary<string, object>
                    {
                        ["resolution"] = "1080p", ["image_resolution"] = "1024x1024", ["image_quality"] = "hd"
                    }
                }
            ]
        };
        var cost = new ModelCost
        {
            Id = 17, PricingModel = PricingModel.RulesBased,
            PricingConfiguration = JsonSerializer.Serialize(config, CorePricingJsonContext.Default.PricingRulesConfig)
        };
        var models = new Mock<IModelCostService>();
        models.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        var calculator = new CostCalculationService(models.Object, NullLogger<CostCalculationService>.Instance,
            new PricingRulesEvaluator(NullLogger<PricingRulesEvaluator>.Instance));
        var billingUsage = providerUsage.CopyForBilling();

        Assert.Equal(0.60m, await calculator.CalculateCostByIdAsync(17, billingUsage));
        Assert.Equal("retained", Assert.Single(providerUsage.PricingParameters).Value);
        Assert.NotSame(providerUsage.PricingParameters, billingUsage.PricingParameters);
        Assert.Equal("1080p", billingUsage.PricingParameters!["resolution"]);
        Assert.Equal("1024x1024", billingUsage.PricingParameters["image_resolution"]);
        Assert.Equal("hd", billingUsage.PricingParameters["image_quality"]);
        Assert.Equal("retained", billingUsage.PricingParameters["PROVIDER_HINT"]);
    }
}
