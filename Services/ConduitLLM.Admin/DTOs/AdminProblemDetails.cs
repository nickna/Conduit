using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.DTOs;

/// <summary>RFC 9457 error response used by Admin endpoint and exception handling.</summary>
public sealed class AdminProblemDetails : ProblemDetails
{
    /// <summary>Stable machine-readable error code.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>Request identifier returned in the x-request-id response header.</summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    /// <summary>Optional field-level validation errors.</summary>
    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
