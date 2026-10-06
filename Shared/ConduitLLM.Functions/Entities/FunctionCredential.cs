using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ConduitLLM.Functions.Entities.Interfaces;
using ConduitLLM.Functions.Enums;

namespace ConduitLLM.Functions.Entities;

/// <summary>
/// Represents an API credential (key) for a function provider type.
/// Supports multiple credentials per provider type for load balancing and failover.
/// Credentials are shared across all function configurations of the same provider type.
/// </summary>
[Table("FunctionCredentials")]
public class FunctionCredential : ICredentialEntity, IIdentifiableEntity<int>
{
    /// <summary>
    /// Unique identifier for this credential
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// The provider type this credential belongs to (e.g., Exa, Tavily)
    /// All function configurations with this provider type can use these credentials
    /// </summary>
    [Required]
    public FunctionProviderType ProviderType { get; set; }

    /// <summary>
    /// Optional owning function configuration. When set, this credential is scoped to a single
    /// configuration (each MCP server has its own token); when null, the credential is shared by
    /// all configurations of <see cref="ProviderType"/> (the Exa/Tavily behavior).
    /// </summary>
    public int? FunctionConfigurationId { get; set; }

    /// <summary>
    /// API key / token for authentication.
    /// May be stored encrypted at rest (config-scoped MCP tokens are protected via
    /// ASP.NET Core Data Protection and carry an <c>enc:v1:</c> prefix); legacy provider-global
    /// credentials remain plaintext until a separate encryption pass migrates them.
    /// Widened from 500 to accommodate the expansion of encrypted payloads.
    /// </summary>
    [StringLength(2000)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional base URL override for this specific credential
    /// Overrides FunctionConfiguration.BaseUrl if set
    /// </summary>
    [StringLength(500)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional organization identifier (if applicable to provider)
    /// </summary>
    [StringLength(200)]
    public string? Organization { get; set; }

    /// <summary>
    /// Function account group identifier (0-32) for intelligent load balancing and failover
    /// Credentials with the same group number share the same external account/quota
    /// Renamed from CredentialGroup to align with ProviderAccountGroup pattern
    /// </summary>
    [Required]
    public short FunctionAccountGroup { get; set; } = 0;

    /// <summary>
    /// Whether this is the primary credential (only one per provider type should be primary)
    /// Primary credential is used by default; non-primary are for failover
    /// </summary>
    [Required]
    public bool IsPrimary { get; set; } = false;

    /// <summary>
    /// Whether this credential is enabled
    /// </summary>
    [Required]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// User-friendly name for this credential (e.g., "Production Key 1")
    /// Renamed from CredentialName to align with ProviderKeyCredential.KeyName pattern
    /// </summary>
    [StringLength(200)]
    public string? KeyName { get; set; }

    /// <summary>
    /// When this credential was created
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this credential was last updated
    /// </summary>
    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
