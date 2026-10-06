using System.Text.Json;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Tests.Core.Services;

public sealed class WebhookIdentityTests
{
    [Fact]
    public void RetryAndReplay_PreserveIdentityAndScope()
    {
        var request = new WebhookDeliveryRequested { VirtualKeyId = 1, WebhookUrl = "https://example.test/callback" };
        var key = WebhookIdentity.DeliveryKey(request);
        Assert.Equal(key, WebhookIdentity.DeliveryKey(request with { RetryCount = 2, DeliveryCycle = 1 }));
        Assert.NotEqual(key, WebhookIdentity.DeliveryKey(request with { VirtualKeyId = 2 }));
        Assert.NotEqual(key, WebhookIdentity.DeliveryKey(request with { WebhookUrl = "https://other.test/callback" }));
    }

    [Fact]
    public void DistinctProgressOccurrences_AreNotCollapsedByTaskAndType()
    {
        var first = new WebhookDeliveryRequested { TaskId = "same", EventType = WebhookEventType.TaskProgress };
        var second = new WebhookDeliveryRequested { TaskId = "same", EventType = WebhookEventType.TaskProgress };
        Assert.NotEqual(WebhookIdentity.DeliveryKey(first), WebhookIdentity.DeliveryKey(second));
    }

    [Fact]
    public void ReservedIdentityHeader_ReplacesAnyCustomCasingWithoutMutatingSnapshot()
    {
        var request = new WebhookDeliveryRequested { Headers = new() { ["x-webhook-id"] = "override", ["Authorization"] = "test" } };
        var headers = WebhookIdentity.Headers(request);
        Assert.Equal(request.EventId, headers[WebhookIdentity.HeaderName]);
        Assert.Equal("override", request.Headers["x-webhook-id"]);
        Assert.Equal("test", headers["Authorization"]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void PreDeploymentMessage_PreservesBaseEventIdentityAndLegacyRetryBudget()
    {
        const string json = """{"EventId":"legacy-event","Timestamp":"2026-10-06T00:00:00Z","TaskId":"task","WebhookUrl":"https://example.test/callback","RetryCount":2,"NextRetryAt":"2026-10-06T01:00:00Z"}""";
        var first = JsonSerializer.Deserialize(json, CoreMessagingJsonContext.Default.WebhookDeliveryRequested)!;
        var second = JsonSerializer.Deserialize(json, CoreMessagingJsonContext.Default.WebhookDeliveryRequested)!;
        Assert.Equal(WebhookIdentity.DeliveryKey(first), WebhookIdentity.DeliveryKey(second));
        Assert.Equal("legacy-event", first.EventId);
        Assert.Equal(2, first.RetryCount);
        Assert.Equal(0, first.DeliveryCycle);
        Assert.Null(first.DeliveryStartedAt);
    }
}
