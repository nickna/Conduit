using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Source-generated metadata for persisted Core Redis reliability payloads.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LegacyWebhookCircuitState))]
internal partial class CoreRedisJsonContext : JsonSerializerContext;
