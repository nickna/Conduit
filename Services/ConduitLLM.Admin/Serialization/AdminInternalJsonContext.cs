using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Models.ProviderSync;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Providers.OpenRouter;

namespace ConduitLLM.Admin.Serialization;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ModelCostExportDto>))]
[JsonSerializable(typeof(List<LogRequestDto>))]
[JsonSerializable(typeof(PricingRulesConfig))]
[JsonSerializable(typeof(Dictionary<string, ParameterDefinition>))]
[JsonSerializable(typeof(PerVideoPricingConfig))]
[JsonSerializable(typeof(PromptCachingConfig))]
[JsonSerializable(typeof(PricingDriftPayload))]
[JsonSerializable(typeof(ContextWindowDriftPayload))]
[JsonSerializable(typeof(CapabilitiesDriftPayload))]
[JsonSerializable(typeof(OpenRouterCatalogResponse))]
[JsonSerializable(typeof(RefundResult))]
[JsonSerializable(typeof(ServiceHeartbeatSnapshot))]
[JsonSerializable(typeof(ServiceHeartbeatStore.LegacyServiceHeartbeatSnapshot))]
internal partial class AdminInternalJsonContext : JsonSerializerContext;

internal static class AdminJson
{
    private static readonly JsonSerializerOptions DefaultOptions = new();

    private static JsonTypeInfo Require(Type type, JsonSerializerOptions options) =>
        new AdminInternalJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type)
        ?? new AdminHttpResponseJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type)
        ?? new AdminHttpJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type)
        ?? throw new NotSupportedException($"Admin JSON contract '{type}' is not registered.");

    public static string Serialize(object value, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(value, Require(value.GetType(), options ?? DefaultOptions));

    public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null) =>
        (T?)JsonSerializer.Deserialize(json, Require(typeof(T), options ?? DefaultOptions));
}
