using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.DTOs;

/// <summary>
/// Request DTO for updating an existing function configuration
/// </summary>
public class UpdateFunctionConfigurationRequest
{
    /// <summary>
    /// User-friendly name for this configuration
    /// </summary>
    [StringLength(200)]
    public string? ConfigurationName { get; set; }

    /// <summary>
    /// Purpose/use case for this function
    /// </summary>
    public FunctionPurpose? Purpose { get; set; }

    /// <summary>
    /// Default execution mode
    /// </summary>
    public ExecutionMode? DefaultExecutionMode { get; set; }

    /// <summary>
    /// Optional custom base URL
    /// </summary>
    [StringLength(500)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Whether this configuration is enabled
    /// </summary>
    public bool? IsEnabled { get; set; }

    /// <summary>
    /// Cache TTL in minutes for function discovery results (null = no caching for this function)
    /// When set, this function's tool definition will be cached for the specified duration.
    /// Requires global Functions.DiscoveryCacheEnabled setting to be true.
    /// </summary>
    [Range(1, 10080)] // 1 minute to 1 week
    public int? CacheTtlMinutes { get; set; }

    /// <summary>
    /// Maximum execution timeout in seconds
    /// </summary>
    [Range(1, 3600)]
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Maximum number of retry attempts
    /// </summary>
    [Range(0, 10)]
    public int? MaxRetries { get; set; }

    /// <summary>
    /// Provider-specific structured settings.
    /// </summary>
    public Dictionary<string, JsonElement>? ProviderSettings { get; set; }

    /// <summary>
    /// JSON Schema object defining parameter requirements for this function.
    /// </summary>
    public Dictionary<string, JsonElement>? ParameterSchema { get; set; }

    /// <summary>
    /// Optional description
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }
}
