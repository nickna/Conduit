using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration.DTOs.VirtualKey;

namespace ConduitLLM.Configuration.Serialization;

/// <summary>
/// Source-generated metadata for JSON documents persisted by configuration services.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, ModelRateLimitDto>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(Guid))]
internal partial class ConfigurationJsonContext : JsonSerializerContext;
