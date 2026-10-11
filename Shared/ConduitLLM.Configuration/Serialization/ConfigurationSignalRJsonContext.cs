using System.Text.Json.Serialization;

using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Contracts.SignalR;

namespace ConduitLLM.Configuration.Serialization;

/// <summary>
/// Source-generated metadata for the live SignalR notification payload families.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ImageGenerationStartedNotification))]
[JsonSerializable(typeof(ImageGenerationProgressNotification))]
[JsonSerializable(typeof(ImageGenerationCompletedNotification))]
[JsonSerializable(typeof(ImageGenerationFailedNotification))]
[JsonSerializable(typeof(ImageGenerationCancelledNotification))]
[JsonSerializable(typeof(VideoGenerationStartedNotification))]
[JsonSerializable(typeof(VideoGenerationProgressNotification))]
[JsonSerializable(typeof(VideoGenerationCompletedNotification))]
[JsonSerializable(typeof(VideoGenerationFailedNotification))]
[JsonSerializable(typeof(VideoGenerationCancelledNotification))]
[JsonSerializable(typeof(PublicVideoGenerationStartedNotification))]
[JsonSerializable(typeof(PublicVideoGenerationProgressNotification))]
[JsonSerializable(typeof(PublicVideoGenerationCompletedNotification))]
[JsonSerializable(typeof(PublicVideoGenerationFailedNotification))]
[JsonSerializable(typeof(TaskSubscriptionNotification))]
[JsonSerializable(typeof(HubErrorNotification))]
[JsonSerializable(typeof(WebhookDeliveryAttempt))]
[JsonSerializable(typeof(WebhookDeliverySuccess))]
[JsonSerializable(typeof(WebhookDeliveryFailure))]
[JsonSerializable(typeof(WebhookRetryInfo))]
[JsonSerializable(typeof(WebhookStatistics))]
[JsonSerializable(typeof(WebhookCircuitBreakerState))]
[JsonSerializable(typeof(VirtualKeyCreatedNotification))]
[JsonSerializable(typeof(VirtualKeyUpdatedNotification))]
[JsonSerializable(typeof(VirtualKeyDeletedNotification))]
[JsonSerializable(typeof(VirtualKeyStatusChangedNotification))]
[JsonSerializable(typeof(VirtualKeyStatusNotification))]
[JsonSerializable(typeof(RateLimitNotification))]
[JsonSerializable(typeof(SystemAnnouncementNotification))]
[JsonSerializable(typeof(ServiceDegradationNotification))]
[JsonSerializable(typeof(ServiceRestorationNotification))]
[JsonSerializable(typeof(ModelMappingNotification))]
[JsonSerializable(typeof(ModelCapabilitiesNotification))]
[JsonSerializable(typeof(ModelAvailabilityNotification))]
[JsonSerializable(typeof(SpendUpdateNotification))]
[JsonSerializable(typeof(BudgetAlertNotification))]
[JsonSerializable(typeof(SpendSummaryNotification))]
[JsonSerializable(typeof(UnusualSpendingNotification))]
public partial class ConfigurationSignalRJsonContext : JsonSerializerContext;
