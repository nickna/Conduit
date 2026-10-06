using System.Text.Json.Serialization;

using ConduitLLM.Security.Models;

namespace ConduitLLM.Security.Serialization;

/// <summary>Source-generated contracts for security data shared through distributed cache.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BannedIpInfo))]
[JsonSerializable(typeof(FailedAuthData))]
[JsonSerializable(typeof(SecurityErrorResponse))]
public partial class SecurityCacheJsonContext : JsonSerializerContext;

public sealed record SecurityErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("code")] int? Code);
