using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Models;

namespace ConduitLLM.Configuration.Serialization;

/// <summary>
/// Source-generated metadata for model capability values persisted in configuration entities.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ProviderOperationalCapabilities))]
internal partial class ConfigurationModelJsonContext : JsonSerializerContext;
