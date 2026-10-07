using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ConduitLLM.Configuration.Entities;

/// <summary>
/// Represents a configured external LLM provider and its credentials.
/// </summary>
/// <remarks>
/// The legacy namespace preserves source compatibility while the persisted model
/// lives in the backend-neutral abstraction assembly.
/// </remarks>
public class Provider
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public ProviderType ProviderType { get; set; } = ProviderType.OpenAI;

    [Required]
    [StringLength(100)]
    public string ProviderName { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    /// <summary>
    /// Non-secret provider-scoped settings persisted as JSONB.
    /// </summary>
    public Dictionary<string, string>? Settings { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool TrustProviderReportedCosts { get; set; }

    public decimal ProviderCostMarkupMultiplier { get; set; } = 1.0m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProviderKeyCredential> ProviderKeyCredentials { get; set; } =
        new List<ProviderKeyCredential>();
}
