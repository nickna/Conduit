using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Source-generated metadata for closed Core service and persistence contracts.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PromptCachingConfig))]
[JsonSerializable(typeof(Dictionary<string, ModelRateLimitRule>))]
[JsonSerializable(typeof(ConnectionPoolWarmingSignal))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(WebhookPayloadTooLarge))]
[JsonSerializable(typeof(ImageCompletionWebhookPayload))]
[JsonSerializable(typeof(ImageProgressWebhookPayload))]
[JsonSerializable(typeof(VideoCompletionWebhookPayload))]
[JsonSerializable(typeof(VideoProgressWebhookPayload))]
[JsonSerializable(typeof(ProviderWarningEntry))]
[JsonSerializable(typeof(ProviderFeedEntry))]
[JsonSerializable(typeof(ModelCost))]
[JsonSerializable(typeof(List<ModelCost>))]
[JsonSerializable(typeof(ModelProviderMapping))]
[JsonSerializable(typeof(List<ModelProviderMapping>))]
[JsonSerializable(typeof(DiscoveryModelsResult))]
[JsonSerializable(typeof(List<Tool>))]
[JsonSerializable(typeof(PricingRulesConfig))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(object))]
internal partial class CoreInternalJsonContext : JsonSerializerContext;

internal sealed record ConnectionPoolWarmingSignal(
    string InstanceId,
    string ServiceType,
    DateTime Timestamp,
    int ConnectionsWarmed);

internal sealed record WebhookPayloadTooLarge(
    string Error,
    int OriginalSizeBytes,
    int MaxSizeBytes,
    bool Truncated);

internal sealed record ProviderWarningEntry(
    string Type,
    string Message,
    DateTime Timestamp);

internal sealed record ProviderFeedEntry(
    int KeyId,
    int ProviderId,
    string Type,
    string Message,
    DateTime Timestamp);
