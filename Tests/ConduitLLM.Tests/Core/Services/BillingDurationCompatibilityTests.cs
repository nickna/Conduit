using System.Globalization;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Helpers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Services;

/// <summary>
/// Published .NET 10 monetary results, including the actual Redis spend-unit boundary.
/// Decimal expectations are deliberately decimal data, independent of compiler double casts.
/// </summary>
[Trait("Component", "Core")]
public class BillingDurationCompatibilityTests
{
    private static readonly string[] Cultures = ["en-US", "fr-FR", "ar-EG"];

    public static TheoryData<string, string, decimal, decimal, long> ChargeCases => new()
    {
        { "audio", "0.07", 0.06m, 0.00007m, 7_000 },
        { "audio", "0.000005", 0.06m, 0.000000005m, 1 },
        { "audio", "0.000015", 0.06m, 0.000000015m, 2 },
        { "audio", "0.000025", 0.06m, 0.000000025m, 3 },
        { "video", "0.3", 0.00000005m, 0.000000015m, 2 },
        { "video", "0.7", 0.00000005m, 0.000000035m, 4 },
        { "video", "1.3", 0.01m, 0.013m, 1_300_000 },
        { "per_second", "0.3", 0.00000005m, 0.000000015m, 2 },
        { "per_unit", "0.3", 0.00000005m, 0.000000015m, 2 },
        { "per_step", "0.3", 0.00000005m, 0.000000015m, 2 }
    };

    [Theory]
    [MemberData(nameof(ChargeCases))]
    public async Task CalculateAndQueueCharge_FractionalDuration_PreservesPublishedAmountAndSpendUnits(
        string kind, string duration, decimal rate, decimal expectedCost, long expectedUnits)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in Cultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var modelCost = CreateModelCost(kind, rate);
                var service = CreateService(modelCost);
                var seconds = double.Parse(duration, CultureInfo.InvariantCulture);
                var usage = kind == "audio"
                    ? new Usage { AudioDurationSeconds = seconds }
                    : new Usage { VideoDurationSeconds = seconds };

                var cost = await service.CalculateCostAsync("duration-model", usage);

                Assert.Equal(expectedCost, cost);
                // Execute the production batching conversion, rather than reproducing its rounding.
                Assert.Equal(expectedUnits, await QueueAndReadSpendUnitsAsync(cost));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    public static TheoryData<bool, string, string, decimal, decimal> RefundCases => new()
    {
        { false, "1", "0.3", 0.00000005m, 0.00000002m },
        { false, "1", "0.1", 0.00000005m, 0m },
        { false, "0.7", "0.3", 0.00000007m, 0.00000003m },
        { false, "1.3", "0.3", 0.00000039m, 0.00000009m },
        { true, "1", "0.3", 0.00000005m, 0.00000002m },
        { true, "1", "0.1", 0.00000005m, 0m },
        { true, "0.7", "0.3", 0.00000007m, 0.00000003m },
        { true, "1.3", "0.3", 0.00000039m, 0.00000009m }
    };

    [Theory]
    [MemberData(nameof(RefundCases))]
    public async Task CalculateRefund_FractionalDuration_PreservesHistoricalProrationAndTiesToEven(
        bool audio, string originalDuration, string refundDuration, decimal charged, decimal expectedRefund)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in Cultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var originalSeconds = double.Parse(originalDuration, CultureInfo.InvariantCulture);
                var refundSeconds = double.Parse(refundDuration, CultureInfo.InvariantCulture);
                var original = audio
                    ? new Usage { AudioDurationSeconds = originalSeconds }
                    : new Usage { VideoDurationSeconds = originalSeconds };
                var refund = audio
                    ? new Usage { AudioDurationSeconds = refundSeconds }
                    : new Usage { VideoDurationSeconds = refundSeconds };
                var modelCosts = new Mock<IModelCostService>(MockBehavior.Strict);
                var service = new CostCalculationService(
                    modelCosts.Object, NullLogger<CostCalculationService>.Instance);

                var result = await service.CalculateRefundAsync(
                    "duration-model", original, refund, "partial", "historical-debit",
                    new ProviderCostRefundContext { OriginalChargedCost = charged });

                Assert.Empty(result.ValidationMessages);
                Assert.True(result.IsPartialRefund);
                Assert.Equal(expectedRefund, result.RefundAmount);
                modelCosts.VerifyNoOtherCalls();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    public static TheoryData<string, decimal> ConversionCases => new()
    {
        { "0", 0m },
        { "-0", 0m },
        { "5E-324", 0m },
        { "-5E-324", 0m },
        { "1E-29", 0m },
        { "-1E-29", 0m },
        { "7E-29", 0.0000000000000000000000000001m },
        { "0.07", 0.07m },
        { "6.2", 6.2m },
        { "1.2345678901234567", 1.23456789012346m },
        { "79228162514264328797450928128", 79228162514264300000000000000m },
        { "-79228162514264328797450928128", -79228162514264300000000000000m }
    };

    [Theory]
    [MemberData(nameof(ConversionCases))]
    public void ConvertDuration_PreservesLegacyPrecisionUnderflowAndCanonicalZero(string duration, decimal expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in Cultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var result = BillingDurationConversion.ToDecimal(double.Parse(duration, CultureInfo.InvariantCulture));
                Assert.Equal(expected, result);
                if (expected == 0m)
                {
                    Assert.Equal(new int[4], decimal.GetBits(result));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    public static TheoryData<double> OverflowCases => new()
    {
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
        double.MaxValue,
        -double.MaxValue,
        Math.ScaleB(1d, 96),
        -Math.ScaleB(1d, 96),
        Math.BitIncrement(Math.ScaleB(1d, 96)),
        Math.BitDecrement(-Math.ScaleB(1d, 96))
    };

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void ConvertDuration_PreservesOverflowAtNonfiniteAndExclusiveDecimalBoundaries(double duration)
    {
        Assert.Throws<OverflowException>(() => BillingDurationConversion.ToDecimal(duration));
    }

    private static ModelCost CreateModelCost(string kind, decimal rate)
    {
        var invariantRate = rate.ToString(CultureInfo.InvariantCulture);
        return new ModelCost
        {
            CostName = "duration-model",
            IsActive = true,
            AudioCostPerMinute = kind == "audio" ? rate : null,
            PricingModel = kind switch
            {
                "audio" => PricingModel.Standard,
                "video" => PricingModel.PerSecondVideo,
                _ => PricingModel.RulesBased
            },
            PricingConfiguration = kind switch
            {
                "audio" => null,
                "video" => $$"""{"baseRate":{{invariantRate}}}""",
                _ => $$"""{"pricingType":"{{kind}}","unitField":"VideoDurationSeconds","defaultRate":{{invariantRate}},"rules":[]}"""
            }
        };
    }

    private static CostCalculationService CreateService(ModelCost modelCost)
    {
        var modelCosts = new Mock<IModelCostService>(MockBehavior.Strict);
        modelCosts.Setup(service => service.GetCostForModelAsync(
                "duration-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelCost);
        return new CostCalculationService(modelCosts.Object, NullLogger<CostCalculationService>.Instance,
            new PricingRulesEvaluator(NullLogger<PricingRulesEvaluator>.Instance));
    }

    private static async Task<long> QueueAndReadSpendUnitsAsync(decimal cost)
    {
        const int keyId = 41;
        const int groupId = 13;
        var runtimeStore = new Mock<IVirtualKeyRuntimeStore>(MockBehavior.Strict);
        runtimeStore.Setup(store => store.GetByIdAsync(keyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyRuntimeRecord
            {
                Id = keyId,
                VirtualKeyGroupId = groupId,
                Group = new VirtualKeyGroupRuntimeRecord { Id = groupId, Balance = 100m }
            });
        var redis = new Mock<IDatabase>(MockBehavior.Strict);
        long? observedUnits = null;
        redis.Setup(database => database.ScriptEvaluateAsync(It.IsAny<string>(),
                It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, keys, values, _) =>
            {
                Assert.Equal(3, keys.Length);
                Assert.Contains($"group:{groupId}", keys[0].ToString());
                Assert.Single(values);
                Assert.Null(observedUnits);
                observedUnits = (long)values[0];
            })
            .ReturnsAsync(RedisResult.Create((RedisValue)1));
        var connection = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        connection.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(redis.Object);
        using var factory = new TestRedisConnectionFactory(connection.Object);
        using var batch = new BatchSpendUpdateService(
            new Mock<IServiceScopeFactory>(MockBehavior.Strict).Object, factory,
            Options.Create(new BatchSpendingOptions()), NullLogger<BatchSpendUpdateService>.Instance,
            new Mock<IBillingAlertingService>().Object, runtimeStore: runtimeStore.Object);

        await batch.QueueSpendUpdateAsync(keyId, cost, new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(observedUnits);
        return observedUnits.Value;
    }
}
