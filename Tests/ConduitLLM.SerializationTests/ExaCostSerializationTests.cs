using System.Text.Json;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Providers.Exa;
using ConduitLLM.Functions.Providers.Exa.Models;
using ConduitLLM.Functions.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class ExaCostSerializationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProviderUsage_PersistsCostBreakdownWithoutReflection(bool contents, bool partial)
    {
        var client = new ExaClient(
            new FunctionConfiguration { ConfigurationName = "fixture", ProviderType = FunctionProviderType.Exa },
            new FunctionCredential { ProviderType = FunctionProviderType.Exa, ApiKey = "fixture-key" },
            null, NullLogger<ExaClient>.Instance);
        var parameters = contents
            ? new Dictionary<string, object> { ["urls"] = new[] { "https://fixture.invalid" } }
            : new Dictionary<string, object> { ["query"] = "fixture" };
        var costs = partial
            ? """{"search":{"neural":0.005},"getText":0,"getHighlights":null,"getSummary":null,"total":null}"""
            : """{"search":{"neural":0.005},"getText":0.001,"getHighlights":0,"getSummary":0.002,"total":0.008123456789}""";
        var usage = client.CalculateUsageFromResponse(parameters, new FunctionExecutionResult
        {
            IsSuccess = true,
            ResponseJson = $$"""{"requestId":"fixture-request","resolvedSearchType":"neural","results":[{"url":"https://fixture.invalid","text":"content"}],"costDollars":{{costs}}}""",
            Duration = TimeSpan.FromMilliseconds(125),
            HttpStatusCode = 200
        });
        Assert.IsType<ExaCostBreakdown>(usage.Metadata!["exaCostBreakdown"]);
        Assert.Equal(1, usage.ResultCount);
        Assert.Equal(1, usage.TextPagesExtracted);
        Assert.Equal(partial ? null : 0.008123456789m, usage.ProviderReportedCost);

        var stored = JsonSerializer.Serialize(new FunctionExecutionCostDetails
        {
            Usage = usage, EstimatedCost = 0.01m,
            ActualCost = usage.ProviderReportedCost ?? 0m, HttpStatusCode = 200
        }, FunctionsJsonContext.Default.FunctionExecutionCostDetails);
        var restored = JsonSerializer.Deserialize(stored, FunctionsJsonContext.Default.FunctionExecutionCostDetails)!;
        Assert.Equal(usage.ProviderReportedCost ?? 0m, restored.ActualCost);
        Assert.Equal(usage.ExecutionDuration, restored.Usage.ExecutionDuration);
        var breakdown = Assert.IsType<JsonElement>(restored.Usage.Metadata!["exaCostBreakdown"]);
        var expectedKeys = partial
            ? new[] { "getText" }
            : new[] { "getText", "getHighlights", "getSummary", "total" };
        if (!contents)
        {
            expectedKeys = [.. expectedKeys, "search"];
            Assert.Equal(0.005m, breakdown.GetProperty("search").GetProperty("neural").GetDecimal());
        }
        Assert.Equal(expectedKeys.Order(), breakdown.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(partial ? 0m : 0.001m, breakdown.GetProperty("getText").GetDecimal());
        if (!partial)
        {
            Assert.Equal(0m, breakdown.GetProperty("getHighlights").GetDecimal());
            Assert.Equal(0.008123456789m, breakdown.GetProperty("total").GetDecimal());
        }
    }
}
