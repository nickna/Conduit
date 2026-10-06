namespace ConduitLLM.Gateway.DTOs;

/// <summary>
/// Observable contract describing the features compiled into the running service variant.
/// </summary>
public sealed record RuntimeCapabilitiesResponse(
    string RuntimeMode,
    string[] SignalRProtocols,
    string PersistenceStatus,
    string[] IncludedFeatures,
    string[] ExcludedFeatures);
