using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wolverine;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class WebhookDurableDeliveryTests(MediaDispatchFixture fixture)
{
    [Fact]
    public async Task AdmissionDenial_PersistsDueTimeWithoutHttpAttempt_HealthyDestinationContinues()
    {
        await fixture.ResetAsync();
        await using var blocked = await WebhookReceiver.StartAsync();
        await using var healthy = await WebhookReceiver.StartAsync();
        var local = new WebhookAdmission(Microsoft.Extensions.Options.Options.Create(new ConduitLLM.Core.Configuration.WebhookDeliveryOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebhookAdmission>.Instance);
        var admission = new Mock<IWebhookAdmission>();
        admission.Setup(a => a.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string url, CancellationToken ct) => url == blocked.Url
                ? Task.FromResult(new WebhookAdmissionDecision(null, DateTime.UtcNow.AddSeconds(30))) : local.AcquireAsync(url, ct));
        fixture.ReceiverAdmission = admission.Object;
        using var host = fixture.Host(worker: false, webhooks: true);
        await host.StartAsync();
        try
        {
            var request = Request(blocked.Url);
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
            var good = Request(healthy.Url);
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(good);
            await DeliveredAsync(WebhookIdentity.DeliveryKey(good));
            await MediaDispatchFixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Db();
                return await db.WebhookDeliveries.AnyAsync(r => r.Id == WebhookIdentity.DeliveryKey(request) &&
                    r.Attempts == 0 && r.ClaimToken == null && r.NextAttemptAt > DateTime.UtcNow);
            });
            Assert.Empty(blocked.Posts); Assert.Single(healthy.Posts);
        }
        finally { await host.StopAsync(); }
    }

    private static WebhookDeliveryRequested Request(string url) => new()
    {
        TaskId = "webhook-durable", TaskType = "video", VirtualKeyId = 1, WebhookUrl = url,
        EventType = WebhookEventType.TaskCompleted, PayloadJson = """{"status":"completed"}""",
        Headers = new() { ["Authorization"] = "Bearer receiver-test", ["x-webhook-id"] = "cannot-override" }
    };

    [Fact]
    public async Task ScheduledRetry_RestartsAfterOutageLongerThanLegacyWindow()
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.Respond = context => { context.Response.StatusCode = 503; return Task.CompletedTask; };
        var request = Request(receiver.Url);
        var id = WebhookIdentity.DeliveryKey(request);
        using (var first = fixture.Host(worker: false, webhooks: true))
        {
            await first.StartAsync();
            try
            {
                await first.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
                await MediaDispatchFixture.EventuallyAsync(async () =>
                {
                    await using var db = fixture.Db();
                    return await db.WebhookDeliveries.AnyAsync(r => r.Id == id && r.Attempts == 1 && r.ClaimToken == null);
                });
            }
            finally { await first.StopAsync(); }
        }
        await Task.Delay(TimeSpan.FromSeconds(15));
        receiver.Respond = context => { context.Response.StatusCode = 202; return Task.CompletedTask; };
        using var restarted = fixture.Host(worker: false, webhooks: true);
        await restarted.StartAsync();
        try
        {
            await DeliveredAsync(id);
            Assert.Equal(2, receiver.Posts.Count);
            Assert.All(receiver.Posts, post =>
            {
                Assert.Equal(request.EventId, post.EventId);
                Assert.Equal("Bearer receiver-test", post.Authorization);
                Assert.Equal(request.PayloadJson, post.Json);
            });
            await using var db = fixture.Db();
            Assert.Equal(202, (await db.WebhookDeliveries.SingleAsync(r => r.Id == id)).LastStatusCode);
        }
        finally { await restarted.StopAsync(); }
    }

    [Fact]
    public async Task TwoWorkers_DuplicateEventsAndReportingFailure_ProduceOnePost()
    {
        await fixture.ResetAsync();
        fixture.WebhookNotifications.Setup(n => n.NotifyDeliverySuccessAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>())).ThrowsAsync(new IOException("SignalR unavailable"));
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.Respond = async context => { await Task.Delay(300); context.Response.StatusCode = 204; };
        using var first = fixture.Host(worker: false, webhooks: true);
        using var second = fixture.Host(worker: false, webhooks: true);
        await first.StartAsync(); await second.StartAsync();
        try
        {
            var request = Request(receiver.Url);
            await Task.WhenAll(first.Services.GetRequiredService<IMessageBus>().PublishAsync(request).AsTask(),
                second.Services.GetRequiredService<IMessageBus>().PublishAsync(request).AsTask());
            await DeliveredAsync(WebhookIdentity.DeliveryKey(request));
            await first.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
            await Task.Delay(500);
            Assert.Single(receiver.Posts);
        }
        finally { await first.StopAsync(); await second.StopAsync(); }
    }

    private async Task DeliveredAsync(string id) => await MediaDispatchFixture.EventuallyAsync(async () =>
    {
        await using var db = fixture.Db();
        return await db.WebhookDeliveries.AnyAsync(r => r.Id == id && r.State == "Delivered");
    });
}
