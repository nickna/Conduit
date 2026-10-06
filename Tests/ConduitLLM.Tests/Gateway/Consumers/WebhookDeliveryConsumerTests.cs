using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Tests.Messaging;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ConduitLLM.Core.Configuration;

using Moq;

namespace ConduitLLM.Tests.Gateway.Consumers;

public sealed class WebhookDeliveryConsumerTests
{
    [Fact]
    public async Task HandleAsync_OpenCircuit_DurablyDefersWithoutAttemptOrPermanentFailure()
    {
        var fixture = new Fixture();
        fixture.CircuitBreaker.Setup(c => c.IsOpen(It.IsAny<string>())).Returns(true);
        await fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context);
        var retry = Assert.IsType<WebhookDeliveryRequested>(Assert.Single(fixture.Context.Scheduled).Event);
        Assert.Equal(0, retry.RetryCount);
        fixture.Store.Verify(s => s.BeginAttemptAsync(It.IsAny<WebhookClaim>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Notifications.Verify(n => n.NotifyDeliveryAttemptAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.Empty(fixture.Webhook.Invocations);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(307)]
    public async Task HandleAsync_PermanentStatus_ExhaustsOnceWithoutScheduledReceiverRetry(int status)
    {
        var fixture = new Fixture();
        fixture.Webhook.Setup(w => w.SendTaskCompletionWebhookAsync(It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<Dictionary<string, string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(WebhookSendResult.Failed(status, "HTTP failure"));
        await Assert.ThrowsAsync<NonRetryableMessageException>(() => fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context));
        Assert.Empty(fixture.Context.Scheduled);
        fixture.Store.Verify(s => s.CompleteAsync(It.IsAny<WebhookClaim>(), It.IsAny<WebhookSendResult>(), true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ReportingFailureAfterCommittedSuccess_DoesNotScheduleOrResend()
    {
        var fixture = new Fixture();
        fixture.Webhook.Setup(w => w.SendTaskCompletionWebhookAsync(It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<Dictionary<string, string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(WebhookSendResult.Ok(204));
        fixture.Notifications.Setup(n => n.NotifyDeliverySuccessAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>())).ThrowsAsync(new IOException("reporting unavailable"));
        await fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context);
        Assert.Empty(fixture.Context.Scheduled);
        fixture.Store.Verify(s => s.CompleteAsync(It.IsAny<WebhookClaim>(), It.IsAny<WebhookSendResult>(), false,
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Webhook.Verify(w => w.SendTaskCompletionWebhookAsync(It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<Dictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SchedulingFailure_PropagatesAndDoesNotExhaust()
    {
        var fixture = new Fixture();
        fixture.CircuitBreaker.Setup(c => c.IsOpen(It.IsAny<string>())).Returns(true);
        fixture.Store.Setup(s => s.ScheduleAsync(It.IsAny<WebhookClaim>(), It.IsAny<DateTime>(),
            It.IsAny<WebhookSendResult?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("database unavailable"));
        await Assert.ThrowsAsync<IOException>(() => fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context));
        fixture.Store.Verify(s => s.CompleteAsync(It.IsAny<WebhookClaim>(), It.IsAny<WebhookSendResult>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_InvalidPayload_ExhaustsWithoutAttempt()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<NonRetryableMessageException>(() => fixture.Consumer.HandleAsync(
            Fixture.Request with { PayloadJson = "{invalid" }, fixture.Context));
        fixture.Store.Verify(s => s.BeginAttemptAsync(It.IsAny<WebhookClaim>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(fixture.Webhook.Invocations);
    }

    [Fact]
    public async Task HandleAsync_Shutdown_LeavesRecoverableClaimAndPropagates()
    {
        var fixture = new Fixture();
        fixture.Webhook.Setup(w => w.SendTaskCompletionWebhookAsync(It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<Dictionary<string, string>?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Consumer.HandleAsync(Fixture.Request, fixture.Context));
        fixture.Store.Verify(s => s.CompleteAsync(It.IsAny<WebhookClaim>(), It.IsAny<WebhookSendResult>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(fixture.Context.Scheduled);
    }

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
        public Mock<IWebhookDeliveryStore> Store { get; } = new();
        public Mock<IWebhookCircuitBreaker> CircuitBreaker { get; } = new();
        public Mock<IWebhookDeliveryNotificationService> Notifications { get; } = new();
        public TestEventContext Context { get; } = new();
        public WebhookDeliveryConsumer Consumer { get; }

        public Fixture()
        {
            var attempts = 0;
            Store.Setup(service => service.TryClaimAsync(It.IsAny<WebhookDeliveryRequested>(), It.IsAny<DateTime>(),
                    It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WebhookDeliveryRequested request, DateTime deadline, TimeSpan lease, CancellationToken token) =>
                    new WebhookClaim(WebhookClaimStatus.Acquired, "test-delivery", Guid.NewGuid(), request, request.RetryCount, deadline));
            Store.Setup(service => service.BeginAttemptAsync(It.IsAny<WebhookClaim>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WebhookClaim claim, CancellationToken _) => (int?)(attempts = claim.Attempts + 1));
            Store.Setup(service => service.CompleteAsync(It.IsAny<WebhookClaim>(), It.IsAny<WebhookSendResult>(),
                    It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Store.Setup(service => service.ScheduleAsync(It.IsAny<WebhookClaim>(), It.IsAny<DateTime>(),
                    It.IsAny<WebhookSendResult?>(), It.IsAny<CancellationToken>()))
                .Returns(async (WebhookClaim claim, DateTime due, WebhookSendResult? _, CancellationToken cancellation) =>
                {
                    await Context.SchedulePublishAsync(due, claim.Request with { RetryCount = attempts, NextRetryAt = due }, cancellation);
                    return true;
                });
            CircuitBreaker.Setup(service => service.IsOpen(It.IsAny<string>())).Returns(false);
            Consumer = new WebhookDeliveryConsumer(
                Webhook.Object,
                Store.Object,
                CircuitBreaker.Object,
                Notifications.Object,
                Mock.Of<ILogger<WebhookDeliveryConsumer>>(),
                new WebhookDeliveryPolicy(Options.Create(new WebhookDeliveryOptions { MaxAttempts = 4 })));
        }
    }
}
