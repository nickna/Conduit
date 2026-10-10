using System.Diagnostics;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class WebhookProcessCrashTests(MediaDispatchFixture fixture)
{
    [Theory]
    [InlineData("image", false)] [InlineData("video", false)]
    [InlineData("image", true)] [InlineData("video", true)]
    public async Task KilledTerminalProducer_StateAndIntentCommitTogether_RecoveryNeverGeneratesOrSpends(string type, bool afterCommit)
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        using var publisher = fixture.Host(worker: false);
        await publisher.StartAsync();
        try
        {
            var metadata = new TaskMetadata(1) { Model = "test-model", WebhookUrl = receiver.Url };
            var submit = publisher.Services.GetRequiredService<IMediaTaskSubmission>();
            var id = type == "image" ? await submit.SubmitAsync(new ImageGenerationRequested { VirtualKeyId = 1,
                Request = new() { Model = "test-model", Prompt = "crash-test" }, WebhookUrl = receiver.Url }, metadata)
                : await submit.SubmitAsync(new VideoGenerationRequested { VirtualKeyId = "1", IsAsync = true,
                    Request = new() { Model = "test-model", Prompt = "crash-test" }, WebhookUrl = receiver.Url }, metadata);
            var repo = fixture.Repository();
            await repo.TryClaimTaskAsync(id, "terminal-crash-test", TimeSpan.FromMinutes(15));
            await repo.MarkProviderInvocationStartedAsync(id, "terminal-crash-test");
            await repo.MarkProviderInvocationCompletedAsync(id, "terminal-crash-test");
            if (!afterCommit) await fixture.SqlAsync("""
                CREATE OR REPLACE FUNCTION pause_terminal_commit() RETURNS trigger AS $$
                BEGIN PERFORM pg_sleep(120); RETURN NEW; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER pause_terminal_commit BEFORE INSERT ON wolverine_media_test.wolverine_outgoing_envelopes
                FOR EACH ROW EXECUTE FUNCTION pause_terminal_commit();
                """);
            try
            {
                using var process = fixture.StartProbe("terminal", id, receiver.Url);
                try
                {
                    Assert.Equal("TERMINAL:starting", await LineAsync(process));
                    if (afterCommit) Assert.Equal("TERMINAL:committed", await LineAsync(process));
                    else await MediaDispatchFixture.EventuallyAsync(async () =>
                    {
                        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
                        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name = 'media-dispatch-probe' AND wait_event = 'PgSleep')", connection);
                        return (bool)(await command.ExecuteScalarAsync())!;
                    });
                }
                finally
                {
                    await KillAsync(process);
                    // PostgreSQL cannot observe a disconnected socket while the
                    // injected pg_sleep is running. Terminate only this probe's
                    // isolated backend so rollback/DDL cleanup need not wait 120s.
                    await fixture.SqlAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'media-dispatch-probe'");
                }
            }
            finally
            {
                if (!afterCommit) await fixture.SqlAsync("DROP TRIGGER pause_terminal_commit ON wolverine_media_test.wolverine_outgoing_envelopes; DROP FUNCTION pause_terminal_commit()");
            }
            await using (var db = fixture.Db())
                Assert.Equal((int)(afterCommit ? TaskState.Completed : TaskState.Processing), (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
            var writer = publisher.Services.GetRequiredService<IMediaTaskTerminalWriter>();
            var committed = await writer.CommitAsync(new(id, TaskState.Completed, "terminal-crash-test", Progress: 100,
                Webhook: new() { TaskId = id, WebhookUrl = receiver.Url, PayloadJson = "{\"status\":\"completed\"}",
                    Headers = new() { ["Authorization"] = "Bearer crash-test" } }));
            Assert.Equal(!afterCommit, committed);
            using var restarted = fixture.Host(worker: false, webhooks: true);
            await restarted.StartAsync();
            try
            {
                await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(receiver.Posts.Count == 1));
                Assert.Equal("Bearer crash-test", Assert.Single(receiver.Posts).Authorization);
                await using var db = fixture.Db();
                Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
                Assert.Empty(fixture.Provider.Invocations);
            }
            finally { await restarted.StopAsync(); }
        }
        finally { await publisher.StopAsync(); }
    }

    [Theory]
    [InlineData("before-send", 1)] [InlineData("after-acceptance", 2)]
    public async Task KilledDeliveryWorker_LeaseRecovers_ReceiverDeduplicatesAmbiguousAcceptance(string pause, int expectedPosts)
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        var accepted = new HashSet<string>();
        receiver.Respond = context =>
        {
            lock (accepted) accepted.Add(context.Request.Headers["X-Webhook-Id"].ToString());
            context.Response.StatusCode = 202; return Task.CompletedTask;
        };
        var request = new WebhookDeliveryRequested { TaskId = "crash-delivery", VirtualKeyId = 1,
            WebhookUrl = receiver.Url, PayloadJson = "{\"status\":\"completed\"}" };
        using var publisher = fixture.Host(worker: false);
        await publisher.StartAsync();
        try
        {
            using var process = fixture.StartProbe("webhook-worker", pause);
            try
            {
                Assert.Equal("WORKER:ready", await LineAsync(process));
                await publisher.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
                Assert.Equal(pause == "before-send" ? "DELIVERY:claimed" : "DELIVERY:accepted", await LineAsync(process));
            }
            finally { await KillAsync(process); }
            using var restarted = fixture.Host(worker: false, webhooks: true);
            await restarted.StartAsync();
            try
            {
                await MediaDispatchFixture.EventuallyAsync(async () =>
                {
                    await using var db = fixture.Db();
                    return await db.WebhookDeliveries.AnyAsync(r => r.Id == WebhookIdentity.DeliveryKey(request) && r.State == "Delivered");
                });
                Assert.Equal(expectedPosts, receiver.Posts.Count);
                Assert.Single(accepted); Assert.All(receiver.Posts, p => Assert.Equal(request.EventId, p.EventId));
                await using var db = fixture.Db();
                Assert.Equal(expectedPosts, (await db.WebhookDeliveries.SingleAsync()).Attempts);
            }
            finally { await restarted.StopAsync(); }
        }
        finally { await publisher.StopAsync(); }
    }

    [Fact]
    public async Task KilledAfterRetryCommitBeforeAcknowledgment_ScheduledRetrySurvives()
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.Respond = context => { context.Response.StatusCode = 503; return Task.CompletedTask; };
        using var publisher = fixture.Host(worker: false);
        await publisher.StartAsync();
        try
        {
            var request = new WebhookDeliveryRequested { TaskId = "scheduled-crash", VirtualKeyId = 1, WebhookUrl = receiver.Url, PayloadJson = "{}" };
            using var process = fixture.StartProbe("webhook-worker", "after-schedule");
            try
            {
                Assert.Equal("WORKER:ready", await LineAsync(process));
                await publisher.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
                Assert.Equal("DELIVERY:scheduled", await LineAsync(process));
            }
            finally { await KillAsync(process); }
            await using (var db = fixture.Db())
            {
                var row = await db.WebhookDeliveries.SingleAsync();
                Assert.Equal(1, row.Attempts); Assert.True(row.NextAttemptAt > row.CreatedAt); Assert.Null(row.ClaimToken);
            }
            receiver.Respond = context => { context.Response.StatusCode = 204; return Task.CompletedTask; };
            using var restarted = fixture.Host(worker: false, webhooks: true);
            await restarted.StartAsync();
            try
            {
                await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db(); return await db.WebhookDeliveries.AnyAsync(r => r.State == "Delivered"); });
                Assert.Equal(2, receiver.Posts.Count);
                Assert.All(receiver.Posts, post => Assert.Equal(request.EventId, post.EventId));
            }
            finally { await restarted.StopAsync(); }
        }
        finally { await publisher.StopAsync(); }
    }

    private static async Task<string?> LineAsync(Process process) => await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    private static async Task KillAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
}
