using System.Text.Json.Serialization;

namespace ConduitLLM.Admin.DTOs;

/// <summary>Describes the supported features of the running Admin service variant.</summary>
public sealed record AdminRuntimeCapabilitiesResponse(
    [property: JsonPropertyName("runtime_mode")] string RuntimeMode,
    [property: JsonPropertyName("signal_r_protocols")] string[] SignalRProtocols,
    [property: JsonPropertyName("persistence_status")] string PersistenceStatus,
    [property: JsonPropertyName("included_features")] string[] IncludedFeatures,
    [property: JsonPropertyName("excluded_features")] string[] ExcludedFeatures);
