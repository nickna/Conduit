using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Tests.Messaging;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Gateway.Consumers;

public sealed class WebhookDeliveryConsumerTests
{
    [Fact]
    public async Task HandleAsync_TerminalFailure_NotifiesOnceAndThrowsNonRetryable()
    {
        var fixture = new Fixture();
        fixture.Webhook
            .Setup(service => service.SendTaskCompletionWebhookAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebhookSendResult.Failed(503, "Endpoint returned 503 Service Unavailable"));
        var request = Fixture.Request with { RetryCount = 3 };

        await Assert.ThrowsAsync<NonRetryableMessageException>(() =>
            fixture.Consumer.HandleAsync(request, fixture.Context));

        Assert.Empty(fixture.Context.Scheduled);
        fixture.Webhook.Verify(service => service.SendTaskCompletionWebhookAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        // The endpoint's actual status code must be forwarded, not null / a fabricated 200
        fixture.Notifications.Verify(service => service.NotifyDeliveryFailureAsync(
            request.WebhookUrl, request.TaskId, It.IsAny<string>(), 503, 4, true), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SuccessfulDelivery_ReportsEndpointsActualStatusCode()
    {
        var fixture = new Fixture();
        fixture.Webhook
            .Setup(service => service.SendTaskCompletionWebhookAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebhookSendResult.Ok(202));

        await fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context);

        // A 202 from the endpoint must be reported as 202, not assumed to be 200
        fixture.Notifications.Verify(service => service.NotifyDeliverySuccessAsync(
            Fixture.Request.WebhookUrl, Fixture.Request.TaskId, 202, It.IsAny<long>(), 1), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_UnexpectedDeliveryError_UsesConsumerManagedRetry()
    {
        var fixture = new Fixture();
        fixture.Webhook
            .Setup(service => service.SendTaskCompletionWebhookAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        await fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context);

        var scheduled = Assert.Single(fixture.Context.Scheduled);
        var retry = Assert.IsType<WebhookDeliveryRequested>(scheduled.Event);
        Assert.Equal(1, retry.RetryCount);
        fixture.Notifications.Verify(service => service.NotifyDeliveryFailureAsync(
            Fixture.Request.WebhookUrl, Fixture.Request.TaskId, It.IsAny<string>(), null, 1, false), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NullPayload_PreservesLegacyFallbackEnvelope()
    {
        var fixture = new Fixture();
        object? deliveredPayload = null;
        fixture.Webhook
            .Setup(service => service.SendTaskCompletionWebhookAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object, Dictionary<string, string>?, CancellationToken>(
                (_, payload, _, _) => deliveredPayload = payload)
            .ReturnsAsync(WebhookSendResult.Ok(200));

        await fixture.Consumer.HandleAsync(
            Fixture.Request with { PayloadJson = "null" },
            fixture.Context);

        var payload = Assert.IsType<JsonElement>(deliveredPayload);
        Assert.Equal("Failed to deserialize payload", payload.GetProperty("error").GetString());
    }

    private sealed class Fixture
    {
        public static WebhookDeliveryRequested Request => new()
        {
            TaskId = "task-1",
            TaskType = "video",
            WebhookUrl = "https://example.test/webhook",
            EventType = WebhookEventType.TaskCompleted,
            PayloadJson = "{}"
        };

        public Mock<IWebhookNotificationService> Webhook { get; } = new();
        public Mock<IWebhookDeliveryTracker> Tracker { get; } = new();
        public Mock<IWebhookCircuitBreaker> CircuitBreaker { get; } = new();
        public Mock<IWebhookDeliveryNotificationService> Notifications { get; } = new();
        public TestEventContext Context { get; } = new();
        public WebhookDeliveryConsumer Consumer { get; }

        public Fixture()
        {
            Tracker.Setup(service => service.IsDeliveredAsync(It.IsAny<string>())).ReturnsAsync(false);
            CircuitBreaker.Setup(service => service.IsOpen(It.IsAny<string>())).Returns(false);
            Consumer = new WebhookDeliveryConsumer(
                Webhook.Object,
                Tracker.Object,
                CircuitBreaker.Object,
                Notifications.Object,
                Mock.Of<ILogger<WebhookDeliveryConsumer>>());
        }
    }
}
