using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ConduitLLM.Configuration.DTOs.VirtualKey;

/// <summary>
/// DTO for requesting updates to an existing virtual key.
/// All properties are optional; only provided values will be updated.
/// </summary>
public class UpdateVirtualKeyRequestDto
{
    private string? _keyName;
    private List<string>? _allowedModels;
    private int? _virtualKeyGroupId;
    private bool? _isEnabled;
    private DateTime? _expiresAt;
    private Dictionary<string, JsonElement>? _metadata;
    private int? _rateLimitRpm;
    private int? _rateLimitRpd;
    private int? _rateLimitTpm;
    private int? _maxParallelRequests;
    private int? _rateLimitPriority;
    private Dictionary<string, ModelRateLimitDto>? _modelRateLimits;

    [StringLength(100, ErrorMessage = "Key name cannot exceed 100 characters.")]
    public string? KeyName
    {
        get => _keyName;
        set { _keyName = value; HasKeyName = true; }
    }

    public List<string>? AllowedModels
    {
        get => _allowedModels;
        set { _allowedModels = value; HasAllowedModels = true; }
    }

    /// <summary>
    /// Optional ID of a different virtual key group to move this key to.
    /// </summary>
    public int? VirtualKeyGroupId
    {
        get => _virtualKeyGroupId;
        set { _virtualKeyGroupId = value; HasVirtualKeyGroupId = true; }
    }

    public bool? IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; HasIsEnabled = true; }
    }

    /// <summary>Omit to keep the expiration; send null to clear it.</summary>
    public DateTime? ExpiresAt
    {
        get => _expiresAt;
        set { _expiresAt = value; HasExpiresAt = true; }
    }

    public Dictionary<string, JsonElement>? Metadata
    {
        get => _metadata;
        set { _metadata = value; HasMetadata = true; }
    }

    public int? RateLimitRpm
    {
        get => _rateLimitRpm;
        set { _rateLimitRpm = value; HasRateLimitRpm = true; }
    }
    public int? RateLimitRpd
    {
        get => _rateLimitRpd;
        set { _rateLimitRpd = value; HasRateLimitRpd = true; }
    }

    /// <summary>
    /// Optional tokens-per-minute ceiling. Omit to keep it; send null to clear it.
    /// </summary>
    public int? RateLimitTpm
    {
        get => _rateLimitTpm;
        set { _rateLimitTpm = value; HasRateLimitTpm = true; }
    }

    /// <summary>
    /// Optional cap on requests in flight at once. Omit to keep it; send null to clear it.
    /// </summary>
    public int? MaxParallelRequests
    {
        get => _maxParallelRequests;
        set { _maxParallelRequests = value; HasMaxParallelRequests = true; }
    }

    /// <summary>
    /// Optional priority tier for saturation-aware group rate limiting: 0 = low (shed first
    /// when the key's group is saturated), 1 = normal, 2 = high. Omit to keep it; send null
    /// to clear it.
    /// </summary>
    [Range(0, 2, ErrorMessage = "RateLimitPriority must be 0 (low), 1 (normal) or 2 (high).")]
    public int? RateLimitPriority
    {
        get => _rateLimitPriority;
        set { _rateLimitPriority = value; HasRateLimitPriority = true; }
    }

    /// <summary>
    /// Per-model rate limit overrides keyed by model alias. Members merge recursively. Omit the
    /// property to keep all overrides; send null to clear them.
    /// </summary>
    public Dictionary<string, ModelRateLimitDto>? ModelRateLimits
    {
        get => _modelRateLimits;
        set { _modelRateLimits = value; HasModelRateLimits = true; }
    }

    [System.Text.Json.Serialization.JsonIgnore] public bool HasKeyName { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasAllowedModels { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasVirtualKeyGroupId { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasIsEnabled { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasExpiresAt { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasMetadata { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasRateLimitRpm { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasRateLimitRpd { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasRateLimitTpm { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasMaxParallelRequests { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasRateLimitPriority { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasModelRateLimits { get; private set; }
}
