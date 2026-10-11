using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>
/// Represents a chunk of a streamed chat completion response.
/// </summary>
public class ChatCompletionChunk
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ProviderToolUsage? ProviderToolUsage { get; set; }
    /// <summary>
    /// Server-only marker for the provider call that produced this usage-bearing chunk.
    /// Repeated provider usage chunks for one iteration intentionally use the same iteration.
    /// </summary>
    [JsonIgnore]
    public ProviderCallUsage? ProviderCallUsage { get; set; }

    /// <summary>
    /// Server-only provider iteration for every agentic chunk, including chunks without usage.
    /// Tool-call indices are scoped to this iteration and the completion choice.
    /// </summary>
    [JsonIgnore]
    public int? ProviderIteration { get; set; }

    /// <summary>
    /// A unique identifier for the chat completion chunk.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>
    /// A list of chat completion choices. Can contain more than one choice if n is greater than 1.
    /// </summary>
    [JsonPropertyName("choices")]
    public List<StreamingChoice> Choices { get; set; } = new();

    /// <summary>
    /// The Unix timestamp (in seconds) of when the chat completion chunk was created.
    /// </summary>
    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Created { get; set; }

    /// <summary>
    /// The model to generate the completion.
    /// </summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// The object type, which is always "chat.completion.chunk".
    /// </summary>
    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk"; // Default value

    /// <summary>
    /// This fingerprint represents the backend configuration that the model runs with.
    /// Can be used in conjunction with the seed request parameter to understand when backend changes have been made that might impact determinism.
    /// </summary>
    [JsonPropertyName("system_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemFingerprint { get; set; }

    /// <summary>
    /// The original model alias used in routing, if different from the model name.
    /// </summary>
    [JsonIgnore]
    public string? OriginalModelAlias { get; set; }

    /// <summary>
    /// Token usage information. Only present in the final chunk of streaming responses
    /// when stream_options.include_usage is true (OpenAI) or for providers that include
    /// usage data in their streaming responses.
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? Usage { get; set; }

    // Note: Usage information is typically not included in chunks,
    // but might appear in the final chunk from some providers or need
    // to be requested separately after the stream finishes.
}
