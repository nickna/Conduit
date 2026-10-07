using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class WebhookRecoveryTests(MediaDispatchFixture fixture)
{
    [Fact]
    public async Task ExhaustedDelivery_InspectAndConcurrentReplay_RetainsIdentityAndAuditsOneCycle()
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.Respond = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        using var host = fixture.Host(worker: false, webhooks: true);
        await host.StartAsync();
        try
        {
            var request = new WebhookDeliveryRequested { TaskId = "replay-test", VirtualKeyId = 1, WebhookUrl = receiver.Url,
                Headers = new() { ["Authorization"] = "Bearer private" }, PayloadJson = """{"secret":"private"}""" };
            var id = WebhookIdentity.DeliveryKey(request);
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
            var recovery = host.Services.GetRequiredService<IWebhookRecovery>();
            await MediaDispatchFixture.EventuallyAsync(async () => (await recovery.InspectAsync(1, "replay-test", null, 1, default))
                .Any(r => r.State == "Exhausted"));
            var row = Assert.Single(await recovery.InspectAsync(1, null, request.EventId, 10, default));
            Assert.Equal(401, row.StatusCode); Assert.Equal(1, row.Attempts);
            Assert.Empty(await recovery.InspectAsync(2, null, request.EventId, 10, default));
            await Task.Delay(1000);
            var diagnostics = await host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>().Stores
                .FetchDeadLetterEnvelopesAsync(new() { Limit = 100 }, default);
            Assert.True(diagnostics.SelectMany(r => r.Envelopes).Any(), "No dead letter: " + string.Join("; ", diagnostics.Select(r => $"{r.DatabaseUri}:{r.TotalCount}")));
            Assert.True((await recovery.DeadLettersAsync(100, default)).Any(l => l.DeliveryId == id),
                "Observed message types: " + string.Join("; ", diagnostics.SelectMany(r => r.Envelopes).Select(e => e.MessageType)));
            var letter = (await recovery.DeadLettersAsync(100, default)).Single(l => l.DeliveryId == id);
            await using (var errors = new ConduitLLM.Messaging.Wolverine.WebhookErrorStore(
                host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(), fixture.ConnectionString, "wolverine_media_test"))
            {
                var adminAdapter = new ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore(
                    host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore>.Instance,
                    errorStore: errors);
                Assert.Contains(await adminAdapter.DeadLettersAsync(100, default), l => l.EnvelopeId == letter.EnvelopeId);
            }
            Assert.Equal("NotFound", (await recovery.ReplayAsync(id, new(Guid.NewGuid(), 2, 0), "operator", default)).Outcome);
            receiver.Respond = c => { c.Response.StatusCode = 204; return Task.CompletedTask; };
            var replay = new WebhookReplayRequest(Guid.NewGuid(), 1, 0, letter.EnvelopeId);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => recovery.ReplayAsync(id, replay, "operator-1", default)));
            Assert.Single(results, r => r.Outcome == "Accepted");
            Assert.All(results, r => Assert.Contains(r.Outcome, new[] { "Accepted", "AlreadyRequested" }));
            await MediaDispatchFixture.EventuallyAsync(async () => (await recovery.InspectAsync(1, "replay-test", null, 1, default))
                .Any(r => r.State == "Delivered" && r.Cycle == 1));
            Assert.Equal(2, receiver.Posts.Count);
            Assert.All(receiver.Posts, p => { Assert.Equal(request.EventId, p.EventId); Assert.Equal(request.PayloadJson, p.Json);
                Assert.Equal("Bearer private", p.Authorization); });
            Assert.Equal("Conflict", (await recovery.ReplayAsync(id, new(Guid.NewGuid(), 1, 1), "operator", default)).Outcome);
            Assert.Equal("AlreadyRequested", (await recovery.ReplayAsync(id, replay, "operator-1", default)).Outcome);
            await using var db = fixture.Db();
            var audit = Assert.Single(await db.WebhookReplayAudits.ToListAsync());
            Assert.Equal("operator-1", audit.Actor); Assert.Equal(1, audit.PreviousAttempts);
            Assert.Equal(replay.OperationId, audit.OperationId);
            Assert.DoesNotContain(await recovery.DeadLettersAsync(100, default), l => l.EnvelopeId == letter.EnvelopeId);
            Assert.Equal(0, (await recovery.BacklogAsync(default)).Pending);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Purge_IsBoundedAndNeverDeletesPendingDelivery()
    {
        await fixture.ResetAsync();
        using var host = fixture.Host(worker: false);
        await host.StartAsync();
        try
        {
            var store = new ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore(host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore>.Instance);
            for (var i = 0; i < 3; i++)
            {
                var claim = await store.TryClaimAsync(new() { TaskId = "purge", WebhookUrl = "https://private.test/?token=secret" },
                    DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1));
                if (i < 2) await store.CompleteAsync(claim, WebhookSendResult.Ok(204), false);
            }
            await fixture.SqlAsync("UPDATE \"WebhookDeliveries\" SET \"RetainUntil\" = CURRENT_TIMESTAMP - INTERVAL '1 day'");
            await store.PurgeAsync(1, default);
            await using var db = fixture.Db();
            Assert.Equal(2, await db.WebhookDeliveries.CountAsync());
            Assert.Single(await db.WebhookDeliveries.Where(r => r.State == "Pending").ToListAsync());
        }
        finally { await host.StopAsync(); }
    }
}
