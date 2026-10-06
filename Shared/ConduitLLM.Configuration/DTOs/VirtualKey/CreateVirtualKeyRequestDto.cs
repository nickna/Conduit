using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ConduitLLM.Configuration.DTOs.VirtualKey;

/// <summary>
/// DTO for requesting the creation of a new virtual key.
/// </summary>
public class CreateVirtualKeyRequestDto
{
    [Required(ErrorMessage = "Key name is required.")]
    [StringLength(100, ErrorMessage = "Key name cannot exceed 100 characters.")]
    public string KeyName { get; set; } = string.Empty;

    public List<string>? AllowedModels { get; set; }

    /// <summary>
    /// Required ID of an existing virtual key group to add this key to.
    /// Create a virtual key group first using POST /api/virtualkey-groups.
    /// </summary>
    [Required(ErrorMessage = "VirtualKeyGroupId is required. Create a virtual key group first using POST /api/virtualkey-groups.")]
    [Range(1, int.MaxValue, ErrorMessage = "VirtualKeyGroupId must be a valid positive number. Create a virtual key group first using POST /api/virtualkey-groups.")]
    public int VirtualKeyGroupId { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public int? RateLimitRpm { get; set; }
    public int? RateLimitRpd { get; set; }

    /// <summary>
    /// Optional tokens-per-minute ceiling. Null leaves the key without a token limit.
    /// </summary>
    public int? RateLimitTpm { get; set; }

    /// <summary>
    /// Optional cap on requests in flight at once. Null leaves the key without a cap.
    /// </summary>
    public int? MaxParallelRequests { get; set; }

    /// <summary>
    /// Optional priority tier for saturation-aware group rate limiting: 0 = low (shed first
    /// when the key's group is saturated), 1 = normal, 2 = high. Null means normal.
    /// </summary>
    [Range(0, 2, ErrorMessage = "RateLimitPriority must be 0 (low), 1 (normal) or 2 (high).")]
    public int? RateLimitPriority { get; set; }

    /// <summary>
    /// Per-model rate limit overrides keyed by model alias. A trailing <c>*</c> matches by
    /// prefix; an exact alias always wins over a prefix rule.
    /// </summary>
    public Dictionary<string, ModelRateLimitDto>? ModelRateLimits { get; set; }
}
