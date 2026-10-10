using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Messaging.Wolverine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Runtime;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class WebhookDeliveryStoreTests(MediaDispatchFixture fixture)
{
    private static WebhookDeliveryRequested Request() => new()
    {
        TaskId = "webhook-test", TaskType = "image", VirtualKeyId = 1,
        WebhookUrl = "https://receiver.test/callback", EventType = WebhookEventType.TaskCompleted,
        PayloadJson = """{"status":"completed"}""", Headers = new() { ["Authorization"] = "Bearer test-only" }
    };

    [Fact]
    public async Task TwoInstances_OneClaimAndAuthoritativeSuccessWithoutRedis()
    {
        using var host1 = fixture.Host(worker: false);
        using var host2 = fixture.Host(worker: false);
        await host1.StartAsync(); await host2.StartAsync();
        try
        {
            var first = Store(host1.Services); var second = Store(host2.Services);
            var request = Request();
            var claims = await Task.WhenAll(first.TryClaimAsync(request, DateTime.UtcNow.AddHours(24), TimeSpan.FromSeconds(40)),
                second.TryClaimAsync(request, DateTime.UtcNow.AddHours(24), TimeSpan.FromSeconds(40)));
            var owner = Assert.Single(claims, c => c.Status == WebhookClaimStatus.Acquired);
            Assert.Single(claims, c => c.Status == WebhookClaimStatus.Busy);
            Assert.Equal(1, await first.BeginAttemptAsync(owner));
            Assert.True(await first.CompleteAsync(owner, WebhookSendResult.Ok(202), exhausted: false));
            Assert.Equal(WebhookClaimStatus.Delivered,
                (await second.TryClaimAsync(request, DateTime.UtcNow.AddHours(24), TimeSpan.FromSeconds(40))).Status);
            await using var db = fixture.Db();
            var record = await db.WebhookDeliveries.SingleAsync(r => r.Id == owner.Id);
            Assert.Equal(1, record.Attempts);
            Assert.Equal(202, record.LastStatusCode);
        }
        finally { await host1.StopAsync(); await host2.StopAsync(); }
    }

    [Fact]
    public async Task ExpiredOwner_CannotOverwriteReplacementClaim()
    {
        using var host = fixture.Host(worker: false);
        await host.StartAsync();
        try
        {
            var store = Store(host.Services); var request = Request();
            var first = await store.TryClaimAsync(request, DateTime.UtcNow.AddHours(24), TimeSpan.FromSeconds(40));
            Assert.Equal(1, await store.BeginAttemptAsync(first));
            await fixture.SqlAsync($"UPDATE \"WebhookDeliveries\" SET \"ClaimExpiresAt\" = now() - interval '1 second' WHERE \"Id\" = '{first.Id}'");
            Assert.False(await store.CompleteAsync(first, WebhookSendResult.Ok(200), false));
            var replacement = await store.TryClaimAsync(request, DateTime.UtcNow.AddHours(24), TimeSpan.FromSeconds(40));
            Assert.Equal(WebhookClaimStatus.Acquired, replacement.Status);
            Assert.NotEqual(first.Token, replacement.Token);
            Assert.Null(await store.BeginAttemptAsync(first));
            Assert.False(await store.CompleteAsync(first, WebhookSendResult.Ok(200), false));
            Assert.Equal(2, await store.BeginAttemptAsync(replacement));
            Assert.True(await store.CompleteAsync(replacement, WebhookSendResult.Ok(204), false));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task ScheduleOutboxFailure_RollsBackStateAndPreservesLeaseRecovery()
    {
        using var host = fixture.Host(worker: false);
        await host.StartAsync();
        try
        {
            var store = Store(host.Services);
            var claim = await store.TryClaimAsync(Request(), DateTime.UtcNow.AddHours(24), TimeSpan.FromSeconds(40));
            await fixture.SqlAsync("""
                CREATE OR REPLACE FUNCTION reject_webhook_schedule() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'injected schedule failure'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_webhook_schedule BEFORE INSERT ON wolverine_media_test.wolverine_incoming_envelopes
                FOR EACH ROW EXECUTE FUNCTION reject_webhook_schedule();
                """);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => store.ScheduleAsync(claim, DateTime.UtcNow.AddMinutes(2)));
                await using var db = fixture.Db();
                Assert.Equal(claim.Token, (await db.WebhookDeliveries.SingleAsync(r => r.Id == claim.Id)).ClaimToken);
            }
            finally
            {
                await fixture.SqlAsync("DROP TRIGGER reject_webhook_schedule ON wolverine_media_test.wolverine_incoming_envelopes; DROP FUNCTION reject_webhook_schedule()");
            }
            Assert.True(await store.ScheduleAsync(claim, DateTime.UtcNow.AddMinutes(2)));
            var next = await store.TryClaimAsync(claim.Request, claim.Deadline, TimeSpan.FromSeconds(40));
            Assert.Equal(WebhookClaimStatus.Busy, next.Status);
            Assert.Equal(0, next.Attempts);
        }
        finally { await host.StopAsync(); }
    }

    private static WebhookDeliveryStore Store(IServiceProvider services) => new(
        services.GetRequiredService<IWolverineRuntime>(), NullLogger<WebhookDeliveryStore>.Instance);
}
