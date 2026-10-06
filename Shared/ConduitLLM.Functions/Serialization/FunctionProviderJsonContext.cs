using System.Text.Json.Serialization;
using System.Text.Json;

using ConduitLLM.Functions.Providers.Exa.Models;
using ConduitLLM.Functions.Providers.Tavily.Models;

namespace ConduitLLM.Functions.Serialization;

/// <summary>Source-generated wire contracts for built-in function providers.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExaSearchRequest))]
[JsonSerializable(typeof(ExaSearchResponse))]
[JsonSerializable(typeof(ExaContentsRequest))]
[JsonSerializable(typeof(ExaContentsResponse))]
[JsonSerializable(typeof(TavilySearchRequest))]
[JsonSerializable(typeof(TavilySearchResponse))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
internal partial class FunctionProviderJsonContext : JsonSerializerContext;
