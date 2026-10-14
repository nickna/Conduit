using System.Text.Json.Serialization;

using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Core.Models.Responses;

/// <summary>Reason a response ended before producing a complete output.</summary>
public sealed record ResponseIncompleteDetails(
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Failure details on the response.failed event.</summary>
public sealed record ResponseFailureDetails(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("provider_error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderErrorDetail? ProviderError = null);
