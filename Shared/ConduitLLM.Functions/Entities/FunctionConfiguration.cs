using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ConduitLLM.Functions.Entities.Interfaces;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.Entities;

/// <summary>
/// Represents a configured instance of a function provider.
/// Multiple configurations of the same provider type can exist (e.g., "Production Exa", "Dev Exa").
/// The Id is the canonical identifier (not ProviderType).
/// </summary>
[Table("FunctionConfigurations")]
public class FunctionConfiguration : IIdentifiableEntity<int>
{
    /// <summary>
    /// Unique identifier for this function configuration (canonical identifier)
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Type of provider (Exa, Perplexity, etc.)
    /// Multiple configurations can share the same ProviderType
    /// </summary>
    [Required]
    public FunctionProviderType ProviderType { get; set; }

    /// <summary>
    /// User-friendly name for this configuration (e.g., "Production Exa Search")
    /// </summary>
    [Required]
    [StringLength(200)]
    public required string ConfigurationName { get; set; }

    /// <summary>
    /// Purpose/use case for this function (Search, Answer, RAG_Search, etc.)
    /// </summary>
    [Required]
    public FunctionPurpose Purpose { get; set; }

    /// <summary>
    /// Default execution mode for this function (Synchronous or Asynchronous)
    /// Can be overridden per execution request
    /// </summary>
    [Required]
    public ExecutionMode DefaultExecutionMode { get; set; } = ExecutionMode.Synchronous;

    /// <summary>
    /// Optional custom base URL for this provider (overrides default)
    /// </summary>
    [StringLength(500)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Whether this function configuration is enabled
    /// </summary>
    [Required]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum execution timeout in seconds (null = no timeout)
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Cache TTL in minutes for function discovery results (null = no caching for this function)
    /// When set, this function's tool definition will be cached for the specified duration.
    /// Requires global Functions.DiscoveryCacheEnabled setting to be true.
    /// </summary>
    public int? CacheTtlMinutes { get; set; }

    /// <summary>
    /// Maximum number of retry attempts on transient failures
    /// </summary>
    public int? MaxRetries { get; set; } = 3;

    /// <summary>
    /// Provider-specific settings stored as JSON
    /// Example: {"defaultNumResults": 10, "useAutoprompt": true}
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ProviderSettings { get; set; }

    /// <summary>
    /// JSON schema defining parameter requirements for this function (UI-focused).
    /// Similar to Model.ModelParameters pattern for dynamic UI generation.
    /// Defines required parameters, optional parameters with types, defaults, and validation rules.
    /// Example: {"required":["query"],"optional":{"numResults":{"type":"number","min":1,"max":100,"default":10}}}
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ParameterSchema { get; set; }

    /// <summary>
    /// Optional description of this function configuration
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// When this configuration was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this configuration was last updated
    /// </summary>
    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties

    /// <summary>
    /// Cost mappings for this function configuration
    /// </summary>
    [JsonIgnore]
    public ICollection<FunctionCostMapping> CostMappings { get; set; } = new List<FunctionCostMapping>();

    /// <summary>
    /// Execution history for this function configuration
    /// </summary>
    [JsonIgnore]
    public ICollection<FunctionExecution> Executions { get; set; } = new List<FunctionExecution>();
}
