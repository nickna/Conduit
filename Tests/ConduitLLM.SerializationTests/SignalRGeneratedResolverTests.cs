using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Serialization;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ConduitLLM.SerializationTests;

public sealed class SignalRGeneratedResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_UsesOnlyGeneratedMetadataAndPreservesWirePolicy(bool gateway)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var options = SignalRTestOptions.Create(gateway);
        Assert.Equal(gateway ? 2 : 1, options.TypeInfoResolverChain.Count);
        Assert.IsType<ConfigurationSignalRJsonContext>(options.TypeInfoResolverChain[0]);
        if (gateway) Assert.IsType<GatewaySignalRJsonContext>(options.TypeInfoResolverChain[1]);
        Assert.Same(JsonNamingPolicy.CamelCase, options.PropertyNamingPolicy);
        Assert.Null(options.DictionaryKeyPolicy);
        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.Equal(JsonIgnoreCondition.Never, options.DefaultIgnoreCondition);
        var protocol = CreateProtocol(options);
        Assert.Throws<NotSupportedException>(() => Write(protocol, "Unknown", new { Unregistered = "fixture" }));
    }

    [Fact]
    public void TaskMetadataAndNestedJsonValues_UseLiveProtocolWithoutReflection()
    {
        var protocol = CreateProtocol(SignalRTestOptions.Create());
        var metadata = new TaskMetadata(7)
        {
            ExtensionData = new()
            {
                ["UPPER_key"] = new Dictionary<string, object>
                {
                    ["flags"] = new object?[] { false, 0, null },
                    ["items"] = new List<object> { "fixture", 1.25 },
                    ["json"] = JsonDocument.Parse("{\"custom\":null}").RootElement.Clone()
                }
            }
        };
        using var document = JsonDocument.Parse(Write(protocol, "TaskStarted", "task", "image_generation", metadata).AsMemory()[..^1]);
        var args = document.RootElement.GetProperty("arguments");
        Assert.Equal(3, args.GetArrayLength());
        Assert.Equal("task", args[0].GetString());
        Assert.Equal("image_generation", args[1].GetString());
        var value = args[2];
        Assert.Equal(7, value.GetProperty("virtualKeyId").GetInt32());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("model").ValueKind);
        var extension = value.GetProperty("UPPER_key");
        Assert.False(extension.GetProperty("flags")[0].GetBoolean());
        Assert.Equal(0, extension.GetProperty("flags")[1].GetInt32());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("flags")[2].ValueKind);
        Assert.Equal("fixture", extension.GetProperty("items")[0].GetString());
        Assert.Equal(1.25, extension.GetProperty("items")[1].GetDouble());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("json").GetProperty("custom").ValueKind);
    }

    [Fact]
    public void DynamicCompletionArgument_RoundTripsAsJsonElementWithExactFields()
    {
        var protocol = CreateProtocol(SignalRTestOptions.Create());
        var invocation = Parse(protocol,
            "{\"type\":1,\"target\":\"TaskCompleted\",\"arguments\":[\"task\",{\"Mixed_KEY\":null,\"zero\":0,\"done\":false,\"urls\":[\"fixture\"]}]}\u001e",
            typeof(string), typeof(object));
        var value = Assert.IsType<JsonElement>(invocation.Arguments[1]);
        using var roundTrip = JsonDocument.Parse(Write(protocol, invocation.Target, invocation.Arguments).AsMemory()[..^1]);
        Assert.Equal(value.GetRawText(), roundTrip.RootElement.GetProperty("arguments")[1].GetRawText());
    }

    [Theory]
    [InlineData("strings")]
    [InlineData("enum")]
    [InlineData("date")]
    [InlineData("notification")]
    public void InboundHubArguments_BindThroughGeneratedMetadata(string kind)
    {
        var protocol = CreateProtocol(SignalRTestOptions.Create());
        switch (kind)
        {
            case "strings":
                var strings = Parse(protocol, "{\"type\":1,\"target\":\"SubscribeToTasks\",\"arguments\":[[\"one\",\"two\"]]}\u001e", typeof(string[]));
                Assert.Equal(new[] { "one", "two" }, Assert.IsType<string[]>(strings.Arguments[0]));
                break;
            case "enum":
                var priority = Parse(protocol, "{\"type\":1,\"target\":\"SystemAnnouncement\",\"arguments\":[\"fixture\",2]}\u001e", typeof(string), typeof(NotificationPriority));
                Assert.Equal(NotificationPriority.High, Assert.IsType<NotificationPriority>(priority.Arguments[1]));
                using (var outgoing = JsonDocument.Parse(Write(protocol, priority.Target, priority.Arguments).AsMemory()[..^1]))
                    Assert.Equal(JsonValueKind.Number, outgoing.RootElement.GetProperty("arguments")[1].ValueKind);
                break;
            case "date":
                var reset = Parse(protocol, "{\"type\":1,\"target\":\"RateLimitWarning\",\"arguments\":[0,\"2026-10-10T01:02:03Z\",\"fixture\"]}\u001e", typeof(int), typeof(DateTime), typeof(string));
                Assert.Equal(new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc), Assert.IsType<DateTime>(reset.Arguments[1]));
                break;
            default:
                var notification = Parse(protocol, "{\"type\":1,\"target\":\"Announcement\",\"arguments\":[{\"MESSAGE\":\"fixture\",\"priority\":0,\"category\":null}]}\u001e", typeof(SystemAnnouncementNotification));
                var announcement = Assert.IsType<SystemAnnouncementNotification>(notification.Arguments[0]);
                Assert.Equal("fixture", announcement.Message);
                Assert.Equal(NotificationPriority.Low, announcement.Priority);
                Assert.Null(announcement.Category);
                break;
        }
    }

    [Fact]
    public void WebhookDeliveryAttempt_ServerNotificationPreservesEveryFieldWithoutReflection()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var protocol = CreateProtocol(SignalRTestOptions.Create());
        var attempt = new WebhookDeliveryAttempt
        {
            WebhookId = "native-webhook", DeliveryId = "native-delivery", Url = "https://native.invalid/hook",
            EventType = "fixture", AttemptNumber = 0, Timestamp = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc),
            TaskId = "task", TaskType = "native-probe"
        };
        using var document = JsonDocument.Parse(Write(protocol, "DeliveryAttempted", attempt).AsMemory()[..^1]);
        var payload = document.RootElement.GetProperty("arguments")[0];
        Assert.Equal(new[] { "attemptNumber", "deliveryId", "eventType", "taskId", "taskType", "timestamp", "url", "webhookId" },
            payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("native-webhook", payload.GetProperty("webhookId").GetString());
        Assert.Equal("native-delivery", payload.GetProperty("deliveryId").GetString());
        Assert.Equal("https://native.invalid/hook", payload.GetProperty("url").GetString());
        Assert.Equal("fixture", payload.GetProperty("eventType").GetString());
        Assert.Equal(0, payload.GetProperty("attemptNumber").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc), payload.GetProperty("timestamp").GetDateTime());
        Assert.Equal("task", payload.GetProperty("taskId").GetString());
        Assert.Equal("native-probe", payload.GetProperty("taskType").GetString());
    }

    [Theory]
    [InlineData("DeliverySucceeded")]
    [InlineData("DeliveryFailed")]
    [InlineData("RetryScheduled")]
    [InlineData("CircuitBreakerStateChanged")]
    public void WebhookServerNotifications_UseGeneratedMetadataAndPreserveEveryField(string target)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var protocol = CreateProtocol(SignalRTestOptions.Create());
        var timestamp = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
        object notification = target switch
        {
            "DeliverySucceeded" => new WebhookDeliverySuccess
            {
                WebhookId = "native-webhook", DeliveryId = "native-delivery", Url = "https://native.invalid/hook",
                EventType = "fixture", StatusCode = 200, ResponseTimeMs = 0.125, AttemptCount = 0,
                Timestamp = timestamp, TaskId = "task", TotalAttempts = 0
            },
            "DeliveryFailed" => new WebhookDeliveryFailure
            {
                WebhookId = "native-webhook", DeliveryId = "native-delivery", Url = "https://native.invalid/hook",
                EventType = "fixture", Error = "", StatusCode = null, AttemptCount = 0, IsFinal = false,
                Timestamp = timestamp, TaskId = "task", ErrorMessage = "fixture failure", AttemptNumber = 0,
                IsPermanentFailure = false
            },
            "RetryScheduled" => new WebhookRetryInfo
            {
                WebhookId = "native-webhook", DeliveryId = "native-delivery", Url = "https://native.invalid/hook",
                EventType = "fixture", NextAttemptNumber = 0, ScheduledAt = timestamp, DelaySeconds = 0,
                Reason = "fixture retry"
            },
            "CircuitBreakerStateChanged" => new WebhookCircuitBreakerState
            {
                Url = "https://native.invalid/hook", PreviousState = "closed", CurrentState = "open",
                Reason = "fixture failure", FailureCount = 0, SuccessCount = 0, NextTestAt = null,
                Timestamp = timestamp
            },
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        using var document = JsonDocument.Parse(Write(protocol, target, notification).AsMemory()[..^1]);
        Assert.Equal(target, document.RootElement.GetProperty("target").GetString());
        var payload = Assert.Single(document.RootElement.GetProperty("arguments").EnumerateArray());
        Assert.Equal("https://native.invalid/hook", payload.GetProperty("url").GetString());
        switch (target)
        {
            case "DeliverySucceeded":
                Assert.Equal(new[] { "attemptCount", "deliveryId", "eventType", "responseTimeMs", "statusCode", "taskId", "timestamp", "totalAttempts", "url", "webhookId" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal("native-webhook", payload.GetProperty("webhookId").GetString());
                Assert.Equal("native-delivery", payload.GetProperty("deliveryId").GetString());
                Assert.Equal("fixture", payload.GetProperty("eventType").GetString());
                Assert.Equal(200, payload.GetProperty("statusCode").GetInt32());
                Assert.Equal(0.125, payload.GetProperty("responseTimeMs").GetDouble());
                Assert.Equal(0, payload.GetProperty("attemptCount").GetInt32());
                Assert.Equal(0, payload.GetProperty("totalAttempts").GetInt32());
                Assert.Equal("task", payload.GetProperty("taskId").GetString());
                Assert.Equal(timestamp, payload.GetProperty("timestamp").GetDateTime());
                break;
            case "DeliveryFailed":
                Assert.Equal(new[] { "attemptCount", "attemptNumber", "deliveryId", "error", "errorMessage", "eventType", "isFinal", "isPermanentFailure", "statusCode", "taskId", "timestamp", "url", "webhookId" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal("native-webhook", payload.GetProperty("webhookId").GetString());
                Assert.Equal("native-delivery", payload.GetProperty("deliveryId").GetString());
                Assert.Equal("fixture", payload.GetProperty("eventType").GetString());
                Assert.Equal("", payload.GetProperty("error").GetString());
                Assert.Equal("fixture failure", payload.GetProperty("errorMessage").GetString());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("statusCode").ValueKind);
                Assert.Equal(0, payload.GetProperty("attemptCount").GetInt32());
                Assert.Equal(0, payload.GetProperty("attemptNumber").GetInt32());
                Assert.False(payload.GetProperty("isFinal").GetBoolean());
                Assert.False(payload.GetProperty("isPermanentFailure").GetBoolean());
                Assert.Equal("task", payload.GetProperty("taskId").GetString());
                Assert.Equal(timestamp, payload.GetProperty("timestamp").GetDateTime());
                break;
            case "RetryScheduled":
                Assert.Equal(new[] { "delaySeconds", "deliveryId", "eventType", "nextAttemptNumber", "reason", "scheduledAt", "url", "webhookId" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal("native-webhook", payload.GetProperty("webhookId").GetString());
                Assert.Equal("native-delivery", payload.GetProperty("deliveryId").GetString());
                Assert.Equal("fixture", payload.GetProperty("eventType").GetString());
                Assert.Equal(0, payload.GetProperty("nextAttemptNumber").GetInt32());
                Assert.Equal(0, payload.GetProperty("delaySeconds").GetDouble());
                Assert.Equal("fixture retry", payload.GetProperty("reason").GetString());
                Assert.Equal(timestamp, payload.GetProperty("scheduledAt").GetDateTime());
                break;
            default:
                Assert.Equal(new[] { "currentState", "failureCount", "nextTestAt", "previousState", "reason", "successCount", "timestamp", "url" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal("open", payload.GetProperty("currentState").GetString());
                Assert.Equal("closed", payload.GetProperty("previousState").GetString());
                Assert.Equal("fixture failure", payload.GetProperty("reason").GetString());
                Assert.Equal(0, payload.GetProperty("failureCount").GetInt32());
                Assert.Equal(0, payload.GetProperty("successCount").GetInt32());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("nextTestAt").ValueKind);
                Assert.Equal(timestamp, payload.GetProperty("timestamp").GetDateTime());
                break;
        }
    }

    [Theory]
    [InlineData("VirtualKeyCreated")]
    [InlineData("VirtualKeyUpdated")]
    [InlineData("VirtualKeyDeleted")]
    [InlineData("VirtualKeyStatusChanged")]
    public void VirtualKeyLifecycleServerNotifications_PreserveGeneratedProtocolContracts(string target)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var protocol = CreateProtocol(SignalRTestOptions.Create());
        var timestamp = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
        object notification = target switch
        {
            "VirtualKeyCreated" => new VirtualKeyCreatedNotification
            {
                KeyId = 42, KeyName = "fixture key", IsEnabled = false, MaxBudget = 0m,
                AllowedModels = null, RateLimitPerMinute = 0, CreatedAt = timestamp, ExpiresAt = null, Timestamp = timestamp
            },
            "VirtualKeyUpdated" => new VirtualKeyUpdatedNotification
            {
                KeyId = 42, KeyName = "fixture key", UpdatedProperties = ["IsEnabled", "MaxBudget"],
                IsEnabled = false, MaxBudget = null, AllowedModels = "", RateLimitPerMinute = 0,
                ExpiresAt = null, UpdatedAt = timestamp, Timestamp = timestamp
            },
            "VirtualKeyDeleted" => new VirtualKeyDeletedNotification
            {
                KeyId = 42, KeyName = "fixture key", DeletedAt = timestamp, DeletionReason = null, Timestamp = timestamp
            },
            "VirtualKeyStatusChanged" => new VirtualKeyStatusChangedNotification
            {
                KeyId = 42, KeyName = "fixture key", PreviousStatus = "enabled", NewStatus = "disabled",
                StatusChangeReason = null, ChangedAt = timestamp, Timestamp = timestamp
            },
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        using var document = JsonDocument.Parse(Write(protocol, target, notification).AsMemory()[..^1]);
        Assert.Equal(target, document.RootElement.GetProperty("target").GetString());
        var payload = Assert.Single(document.RootElement.GetProperty("arguments").EnumerateArray());
        Assert.Equal(42, payload.GetProperty("keyId").GetInt32());
        Assert.Equal("fixture key", payload.GetProperty("keyName").GetString());
        Assert.Equal(timestamp, payload.GetProperty("timestamp").GetDateTime());
        switch (target)
        {
            case "VirtualKeyCreated":
                Assert.Equal(new[] { "allowedModels", "createdAt", "expiresAt", "isEnabled", "keyId", "keyName", "maxBudget", "rateLimitPerMinute", "timestamp" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.False(payload.GetProperty("isEnabled").GetBoolean());
                Assert.Equal(0m, payload.GetProperty("maxBudget").GetDecimal());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("allowedModels").ValueKind);
                Assert.Equal(0, payload.GetProperty("rateLimitPerMinute").GetInt32());
                Assert.Equal(timestamp, payload.GetProperty("createdAt").GetDateTime());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("expiresAt").ValueKind);
                break;
            case "VirtualKeyUpdated":
                Assert.Equal(new[] { "allowedModels", "expiresAt", "isEnabled", "keyId", "keyName", "maxBudget", "rateLimitPerMinute", "timestamp", "updatedAt", "updatedProperties" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal(new[] { "IsEnabled", "MaxBudget" }, payload.GetProperty("updatedProperties")
                    .EnumerateArray().Select(value => value.GetString()));
                Assert.False(payload.GetProperty("isEnabled").GetBoolean());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("maxBudget").ValueKind);
                Assert.Equal("", payload.GetProperty("allowedModels").GetString());
                Assert.Equal(0, payload.GetProperty("rateLimitPerMinute").GetInt32());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("expiresAt").ValueKind);
                Assert.Equal(timestamp, payload.GetProperty("updatedAt").GetDateTime());
                break;
            case "VirtualKeyDeleted":
                Assert.Equal(new[] { "deletedAt", "deletionReason", "keyId", "keyName", "timestamp" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal(timestamp, payload.GetProperty("deletedAt").GetDateTime());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("deletionReason").ValueKind);
                break;
            default:
                Assert.Equal(new[] { "changedAt", "keyId", "keyName", "newStatus", "previousStatus", "statusChangeReason", "timestamp" },
                    payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal("enabled", payload.GetProperty("previousStatus").GetString());
                Assert.Equal("disabled", payload.GetProperty("newStatus").GetString());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("statusChangeReason").ValueKind);
                Assert.Equal(timestamp, payload.GetProperty("changedAt").GetDateTime());
                break;
        }
    }

    [Fact]
    public void SharedNotificationTimeSpan_PreservesStandardSignalRWireFormat()
    {
        var protocol = CreateProtocol(SignalRTestOptions.Create(false));
        using var document = JsonDocument.Parse(Write(protocol, "ServiceRestored", new ServiceRestorationNotification
        {
            Service = "fixture", OutageDuration = TimeSpan.FromMilliseconds(1250), Priority = NotificationPriority.Low
        }).AsMemory()[..^1]);
        var value = document.RootElement.GetProperty("arguments")[0];
        Assert.Equal("00:00:01.2500000", value.GetProperty("outageDuration").GetString());
        Assert.Equal(0, value.GetProperty("priority").GetInt32());
    }

    private static JsonHubProtocol CreateProtocol(JsonSerializerOptions options) => new(Options.Create(new JsonHubProtocolOptions
    {
        PayloadSerializerOptions = options
    }));

    private static byte[] Write(JsonHubProtocol protocol, string target, params object?[] arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage(target, arguments), buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static InvocationMessage Parse(JsonHubProtocol protocol, string frame, params Type[] argumentTypes)
    {
        var input = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(frame));
        Assert.True(protocol.TryParseMessage(ref input, new FixtureBinder(argumentTypes), out var message));
        Assert.True(input.IsEmpty);
        return Assert.IsType<InvocationMessage>(message);
    }

    private sealed class FixtureBinder(Type[] types) : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => types;
        public Type GetReturnType(string invocationId) => typeof(object);
        public Type GetStreamItemType(string streamId) => typeof(object);
    }
}

internal static class SignalRTestOptions
{
    public static JsonSerializerOptions Create(bool gateway = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduitSignalR(new FixtureEnvironment(), null, "fixture:", 2, "fixture",
            additionalPayloadResolver: gateway ? GatewaySignalRJsonContext.Default : null);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
    }

    private sealed class FixtureEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "fixture";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
