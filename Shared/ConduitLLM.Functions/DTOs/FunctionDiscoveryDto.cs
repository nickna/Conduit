using System.Text.Json;

namespace ConduitLLM.Functions.DTOs;

/// <summary>
/// Discovered function configuration for virtual key holders.
/// Provides metadata about available functions without exposing credentials.
/// </summary>
public class FunctionDiscoveryDto
{
    /// <summary>
    /// The function configuration ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// User-friendly name for this configuration.
    /// </summary>
    public string ConfigurationName { get; set; } = string.Empty;

    /// <summary>
    /// Provider type (e.g., "Exa", "Perplexity").
    /// </summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>
    /// Function purpose ("Search", "Answer", "ContentRetrieval" or "RAG").
    /// </summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of this function configuration.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Default execution mode (Synchronous or Asynchronous).
    /// </summary>
    public string DefaultExecutionMode { get; set; } = string.Empty;

    /// <summary>
    /// Whether this function configuration is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Maximum execution timeout in seconds (null = no timeout).
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// JSON Schema object describing accepted execution input.
    /// </summary>
    public Dictionary<string, JsonElement> ParameterSchema { get; set; } = new();
}

/// <summary>
/// Response model for function discovery list.
/// </summary>
public class FunctionDiscoveryResponse
{
    /// <summary>
    /// List of available function configurations.
    /// </summary>
    public List<FunctionDiscoveryDto> Functions { get; set; } = new();

    /// <summary>
    /// Total count of available functions.
    /// </summary>
    public int Count { get; set; }
}
