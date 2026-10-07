using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents one credential for a configured provider.
/// </summary>
public class ProviderKeyCredential
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int ProviderId { get; set; }

    public Provider Provider { get; set; } = null!;

    [Range(0, 32)]
    public short ProviderAccountGroup { get; set; }

    /// <summary>
    /// Encrypted API key payload. Legacy plaintext values remain readable and are
    /// protected by the application on their next update.
    /// </summary>
    public string? ApiKey { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>
    /// Encrypted structured secret settings persisted as JSONB.
    /// </summary>
    public Dictionary<string, string>? SecretSettings { get; set; }

    public string? KeyName { get; set; }

    public bool IsPrimary { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
