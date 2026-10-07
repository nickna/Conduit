using System.Text.Json.Serialization;

using ConduitLLM.Configuration.DTOs.SignalR;

namespace ConduitLLM.Configuration.Serialization;

/// <summary>
/// Source-generated metadata for the live SignalR notification payload families.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(string[]))]
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
