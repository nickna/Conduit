using System.Text.Json;

using ConduitLLM.Core.Serialization;

using AwesomeAssertions;

namespace ConduitLLM.Tests.Core.Serialization;

public sealed class CorePricingJsonContextTests
{
    [Fact]
    public void PersistedPricingShapes_DeserializeCamelCaseWithoutReflectionFallback()
    {
        JsonSerializer.Deserialize(
                """{"rates":{"720p_6":0.28}}""",
                CorePricingJsonContext.Default.PerVideoPricingConfig)!
            .Rates["720p_6"].Should().Be(0.28m);

        JsonSerializer.Deserialize(
                """{"baseRate":0.09,"resolutionMultipliers":{"1080p":1.5}}""",
                CorePricingJsonContext.Default.PerSecondVideoPricingConfig)!
            .ResolutionMultipliers["1080p"].Should().Be(1.5m);

        JsonSerializer.Deserialize(
                """{"costPerStep":0.003,"defaultSteps":20,"modelSteps":{"fast":8}}""",
                CorePricingJsonContext.Default.InferenceStepsPricingConfig)!
            .ModelSteps!["fast"].Should().Be(8);

        JsonSerializer.Deserialize(
                """{"tiers":[{"maxContext":4096,"inputCost":1.25,"outputCost":2.5}]}""",
                CorePricingJsonContext.Default.TieredTokensPricingConfig)!
            .Tiers.Should().ContainSingle(tier =>
                tier.MaxContext == 4096 && tier.InputCost == 1.25m && tier.OutputCost == 2.5m);

        JsonSerializer.Deserialize(
                """{"baseRate":0.04,"qualityMultipliers":{"hd":2},"resolutionMultipliers":{"1024x1024":1.25}}""",
                CorePricingJsonContext.Default.PerImagePricingConfig)!
            .QualityMultipliers!["hd"].Should().Be(2m);
    }

    [Fact]
    public void RulesConfig_PreservesDynamicConditionValuesAsJsonElements()
    {
        const string json = """
            {
              "version":"1.0",
              "pricingType":"per_second",
              "defaultRate":0.1,
              "rules":[{
                "conditions":{"resolution":"1080p","with_audio":true,"steps":12},
                "rate":0.25,
                "priority":10
              }]
            }
            """;

        var config = JsonSerializer.Deserialize(
            json,
            CorePricingJsonContext.Default.PricingRulesConfig)!;

        config.PricingType.Should().Be("per_second");
        config.Rules.Should().ContainSingle();
        config.Rules[0].Conditions["resolution"].Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("1080p");
        config.Rules[0].Conditions["with_audio"].Should().BeOfType<JsonElement>()
            .Which.GetBoolean().Should().BeTrue();
        config.Rules[0].Conditions["steps"].Should().BeOfType<JsonElement>()
            .Which.GetInt32().Should().Be(12);
    }

    [Fact]
    public void Metadata_RemainsCaseInsensitiveForLegacyPascalCasePayloads()
    {
        var config = JsonSerializer.Deserialize(
            """{"BaseRate":0.11,"ResolutionMultipliers":{"720p":1}}""",
            CorePricingJsonContext.Default.PerSecondVideoPricingConfig);

        config!.BaseRate.Should().Be(0.11m);
        config.ResolutionMultipliers["720p"].Should().Be(1m);
    }
}
