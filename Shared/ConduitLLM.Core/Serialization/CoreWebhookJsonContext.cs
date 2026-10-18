using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Serialization;

/// <summary>Generated contracts for durable webhook bodies and receiver requests.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageCompletionWebhookPayload))]
[JsonSerializable(typeof(ImageProgressWebhookPayload))]
[JsonSerializable(typeof(VideoCompletionWebhookPayload))]
[JsonSerializable(typeof(VideoProgressWebhookPayload))]
[JsonSerializable(typeof(WebhookPayloadTooLarge))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(decimal))]
public partial class CoreWebhookJsonContext : JsonSerializerContext;

/// <summary>Size-limit envelope retained in place of an oversized webhook body.</summary>
public sealed record WebhookPayloadTooLarge(
    string Error,
    int OriginalSizeBytes,
    int MaxSizeBytes,
    bool Truncated);
