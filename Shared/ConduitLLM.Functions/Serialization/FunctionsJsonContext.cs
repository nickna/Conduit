using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Models.Pricing;
using ConduitLLM.Functions.Providers.Mcp;

namespace ConduitLLM.Functions.Serialization;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(FunctionCost))]
[JsonSerializable(typeof(List<FunctionCost>))]
[JsonSerializable(typeof(FunctionExecutionRequestData))]
[JsonSerializable(typeof(FunctionExecutionCostDetails))]
[JsonSerializable(typeof(TieredPricingConfig))]
[JsonSerializable(typeof(ExaHybridPricingConfig))]
[JsonSerializable(typeof(TavilySearchPricingConfig))]
[JsonSerializable(typeof(PerplexityHybridPricingConfig))]
[JsonSerializable(typeof(McpServerSettings))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
public partial class FunctionsJsonContext : JsonSerializerContext;

public sealed class FunctionExecutionRequestData
{
    public required Dictionary<string, object> Parameters { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed class FunctionExecutionCostDetails
{
    public required FunctionExecutionUsage Usage { get; init; }
    public decimal EstimatedCost { get; init; }
    public decimal ActualCost { get; init; }
    public int? HttpStatusCode { get; init; }
}
