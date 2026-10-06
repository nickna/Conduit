using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Events;
using ConduitLLM.Core.Events;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Source-generated metadata for every message in the Gateway/Admin Wolverine topology.
/// Wolverine's existing default JSON shape is retained (Pascal-case CLR property names,
/// numeric enums, and explicit <see cref="JsonPropertyNameAttribute"/> overrides).
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WebhookDeliveryRequested))]
[JsonSerializable(typeof(SpendUpdateRequested))]
[JsonSerializable(typeof(VideoGenerationRequested))]
[JsonSerializable(typeof(VideoGenerationCancelled))]
[JsonSerializable(typeof(ImageGenerationRequested))]
[JsonSerializable(typeof(ImageGenerationCancelled))]
[JsonSerializable(typeof(VirtualKeyUpdated))]
[JsonSerializable(typeof(VirtualKeyCreated))]
[JsonSerializable(typeof(VirtualKeyDeleted))]
[JsonSerializable(typeof(SpendUpdated))]
[JsonSerializable(typeof(SpendThresholdExceeded))]
[JsonSerializable(typeof(ProviderCreated))]
[JsonSerializable(typeof(ProviderUpdated))]
[JsonSerializable(typeof(ProviderDeleted))]
[JsonSerializable(typeof(ModelUpdated))]
[JsonSerializable(typeof(DiscoveryCacheInvalidationRequested))]
[JsonSerializable(typeof(AsyncTaskCreated))]
[JsonSerializable(typeof(AsyncTaskUpdated))]
[JsonSerializable(typeof(AsyncTaskDeleted))]
[JsonSerializable(typeof(MediaGenerationCompleted))]
[JsonSerializable(typeof(VideoGenerationStarted))]
[JsonSerializable(typeof(MediaCleanupAlertRaised))]
[JsonSerializable(typeof(ModelMappingChanged))]
[JsonSerializable(typeof(ModelCostChanged))]
[JsonSerializable(typeof(IpFilterChanged))]
[JsonSerializable(typeof(ProviderToolChanged))]
[JsonSerializable(typeof(ProviderKeyCredentialCreated))]
[JsonSerializable(typeof(ProviderKeyCredentialUpdated))]
[JsonSerializable(typeof(ProviderKeyCredentialDeleted))]
[JsonSerializable(typeof(ProviderKeyCredentialPrimaryChanged))]
[JsonSerializable(typeof(ProviderKeyDisabledEvent))]
[JsonSerializable(typeof(ProviderKeyReenabledEvent))]
[JsonSerializable(typeof(ImageGenerationProgress))]
[JsonSerializable(typeof(ImageGenerationCompleted))]
[JsonSerializable(typeof(ImageGenerationFailed))]
[JsonSerializable(typeof(VideoGenerationProgress))]
[JsonSerializable(typeof(VideoGenerationCompleted))]
[JsonSerializable(typeof(VideoGenerationFailed))]
[JsonSerializable(typeof(IndeterminateMediaTaskRetryRequested))]
[JsonSerializable(typeof(BatchSpendFlushRequestedEvent))]
[JsonSerializable(typeof(GlobalSettingChanged))]
[JsonSerializable(typeof(GlobalSettingsReloadRequested))]
[JsonSerializable(typeof(FunctionConfigurationChanged))]
[JsonSerializable(typeof(FunctionDiscoveryCacheInvalidationRequested))]
[JsonSerializable(typeof(GatewayHeartbeat))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
public partial class CoreMessagingJsonContext : JsonSerializerContext;
