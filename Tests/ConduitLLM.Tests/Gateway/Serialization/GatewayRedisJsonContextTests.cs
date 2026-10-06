using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Tests.Gateway.Serialization;

public sealed class GatewayRedisJsonContextTests
{
    [Fact]
    public void CachedModelCostRoundTripPreservesTypedPricingConfiguration()
    {
        var value = new CachedModelCost
        {
            Id = 7,
            CostName = "video",
            PricingModel = PricingModel.PerVideo,
            ParsedPricingConfiguration = new PerVideoPricingConfig
            {
                Rates = new Dictionary<string, decimal> { ["720p_5"] = 0.25m }
            }
        };

        var json = JsonSerializer.Serialize(
            value,
            GatewayRedisJsonContext.Default.CachedModelCost);
        var roundTrip = JsonSerializer.Deserialize(
            json,
            GatewayRedisJsonContext.Default.CachedModelCost);

        Assert.NotNull(roundTrip);
        var configuration = Assert.IsType<JsonElement>(roundTrip.ParsedPricingConfiguration);
        Assert.Equal(
            0.25m,
            configuration.GetProperty("Rates").GetProperty("720p_5").GetDecimal());
    }
}
