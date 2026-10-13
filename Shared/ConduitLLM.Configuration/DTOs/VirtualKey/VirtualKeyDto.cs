using ConduitLLM.Configuration.Utilities;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ConduitLLM.Configuration.DTOs.VirtualKey;

/// <summary>
/// Data Transfer Object representing a Virtual Key for API responses, excluding sensitive hash.
/// </summary>
public class VirtualKeyDto : IVersionedRepresentation
{
    /// <summary>Version of the database snapshot represented by this response.</summary>
    [JsonIgnore]
    public string? ResourceETag { get; set; }

    /// <summary>
    /// Unique identifier for the virtual key.
    /// </summary>
    [Required] public int Id { get; set; }

    /// <summary>
    /// Human-readable name for the virtual key.
    /// </summary>
    [Required]
    public string KeyName { get; set; } = string.Empty;

    // Note: KeyHash is intentionally excluded for security reasons

    /// <summary>
    /// A prefix of the virtual key (e.g., "condt_...") for display purposes. 
    /// The full key is not exposed after creation.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Model IDs that this key is allowed to access.
    /// Empty or null means all models are allowed.
    /// </summary>
    public List<string>? AllowedModels { get; set; }

    /// <summary>
    /// ID of the virtual key group this key belongs to.
    /// </summary>
    [Required] public int VirtualKeyGroupId { get; set; }

    /// <summary>
    /// Indicates whether the key is currently active and can be used for API calls.
    /// </summary>
    [Required] public bool IsEnabled { get; set; }

    /// <summary>
    /// Optional expiration date for the key.
    /// After this date, the key will no longer be valid even if IsEnabled is true.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Date and time when the key was created.
    /// </summary>
    [Required] public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Date and time when the key was last updated.
    /// </summary>
    [Required] public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Optional structured metadata associated with this key.
    /// Can be used to store additional information about the key's purpose or owner.
    /// </summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>
    /// Optional rate limit in requests per minute.
    /// </summary>
    public int? RateLimitRpm { get; set; }

    /// <summary>
    /// Optional rate limit in requests per day.
    /// </summary>
    public int? RateLimitRpd { get; set; }

    /// <summary>
    /// Optional rate limit in tokens per minute (prompt plus completion).
    /// </summary>
    public int? RateLimitTpm { get; set; }

    /// <summary>
    /// Optional cap on the number of requests this key may have in flight at once.
    /// </summary>
    public int? MaxParallelRequests { get; set; }

    /// <summary>
    /// Priority tier for saturation-aware group rate limiting: 0 = low (shed first when the
    /// key's group is saturated), 1 = normal, 2 = high. Null means normal.
    /// </summary>
    public int? RateLimitPriority { get; set; }

    /// <summary>
    /// Per-model rate limit overrides keyed by model alias.
    /// </summary>
    public Dictionary<string, ModelRateLimitDto>? ModelRateLimits { get; set; }

    /// <summary>
    /// Optional description of the key's purpose
    /// </summary>
    public string? Description { get; set; }

}
