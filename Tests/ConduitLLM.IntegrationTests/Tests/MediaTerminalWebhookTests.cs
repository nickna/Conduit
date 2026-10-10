using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using ConduitLLM.Core.Services;
using ConduitLLM.Configuration.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "Webhooks")]
public sealed class MediaTerminalWebhookTests(MediaDispatchFixture fixture)
{
    private const string Worker = "terminal-webhook-worker";

    [Theory]
    [InlineData("image")] [InlineData("video")]
    public async Task AcceptedMedia_RealOrchestratorSendsOriginalHeadersAndPayload_ProviderAndSpendRunOnce(string type)
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        using var host = fixture.Host(worker: true, webhooks: true);
        await host.StartAsync();
        try
        {
            var metadata = new TaskMetadata(1) { Model = "test-model", Prompt = "test", WebhookUrl = receiver.Url,
                ExtensionData = new() { ["VirtualKey"] = "test-key" } };
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer accepted-media" };
            var submission = host.Services.GetRequiredService<IMediaTaskSubmission>();
            var id = type == "image" ? await submission.SubmitAsync(new ImageGenerationRequested { VirtualKeyId = 1,
                Request = new() { Model = "test-model", Prompt = "test" }, WebhookUrl = receiver.Url, WebhookHeaders = headers }, metadata)
                : await submission.SubmitAsync(new VideoGenerationRequested { VirtualKeyId = "1", IsAsync = true,
                    Request = new() { Model = "test-model", Prompt = "test" }, WebhookUrl = receiver.Url, WebhookHeaders = headers }, metadata);
            await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db();
                return receiver.Posts.Any(p => p.Json.Contains("completed")) && await db.VirtualKeyGroupTransactions.AnyAsync(); });
            var post = Assert.Single(receiver.Posts, p => p.Json.Contains("completed"));
            Assert.Equal("Bearer accepted-media", post.Authorization);
            using var payload = System.Text.Json.JsonDocument.Parse(post.Json);
            Assert.Equal(id, payload.RootElement.GetProperty("task_id").GetString());
            Assert.Equal("completed", payload.RootElement.GetProperty("status").GetString());
            await using var db = fixture.Db();
            Assert.Single(await db.VirtualKeyGroupTransactions.ToListAsync());
            Assert.Equal(99.99m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
            if (type == "image") fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            else fixture.Provider.As<IVideoGenerationClient>().Verify(p => p.CreateVideoAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("image", TaskState.Completed)] [InlineData("video", TaskState.Completed)]
    [InlineData("image", TaskState.Failed)] [InlineData("video", TaskState.Failed)]
    [InlineData("image", TaskState.Cancelled)] [InlineData("video", TaskState.Cancelled)]
    public async Task TerminalCommit_RestartDeliversOneOriginalCallbackWithoutProviderWork(string type, TaskState state)
    {
        await fixture.ResetAsync();
        fixture.Cache.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Optional cache unavailable"));
        await using var receiver = await WebhookReceiver.StartAsync();
        string id;
        using (var publisher = fixture.Host(worker: false, notificationsFail: true))
        {
            await publisher.StartAsync();
            try
            {
                id = await AcceptAndClaimAsync(publisher, type, receiver.Url);
                var webhook = new WebhookDeliveryRequested { TaskId = id, TaskType = type, WebhookUrl = receiver.Url,
                    EventType = state == TaskState.Completed ? WebhookEventType.TaskCompleted :
                        state == TaskState.Failed ? WebhookEventType.TaskFailed : WebhookEventType.TaskCancelled,
                    PayloadJson = $$"""{"task_id":"{{id}}","status":"{{state.ToString().ToLowerInvariant()}}"}""",
                    Headers = new() { ["Authorization"] = "Bearer terminal-test" } };
                var transition = new MediaTaskTerminalTransition(id, state, Worker, Webhook: webhook);
                var writer = publisher.Services.GetRequiredService<IMediaTaskTerminalWriter>();
                var results = await Task.WhenAll(writer.CommitAsync(transition), writer.CommitAsync(transition));
                Assert.Single(results, committed => committed);
                await using var db = fixture.Db();
                Assert.Equal((int)state, (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
            }
            finally { await publisher.StopAsync(); }
        }
        using var restarted = fixture.Host(worker: false, webhooks: true);
        await restarted.StartAsync();
        try
        {
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(receiver.Posts.Count == 1));
            var post = Assert.Single(receiver.Posts);
            Assert.Equal("Bearer terminal-test", post.Authorization);
            Assert.Equal(64, post.EventId.Length);
            Assert.Contains(state.ToString().ToLowerInvariant(), post.Json);
            Assert.Empty(fixture.Provider.Invocations);
            await using var db = fixture.Db();
            Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
        }
        finally { await restarted.StopAsync(); }
    }

    [Fact]
    public async Task OutboxFailure_RollsBackTerminalStateAndCallbackTogether()
    {
        await fixture.ResetAsync();
        using var publisher = fixture.Host(worker: false);
        await publisher.StartAsync();
        try
        {
            var id = await AcceptAndClaimAsync(publisher, "image", "https://receiver.test/callback");
            await fixture.SqlAsync("""
                CREATE OR REPLACE FUNCTION reject_terminal_webhook() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'terminal outbox fault'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_terminal_webhook BEFORE INSERT ON wolverine_media_test.wolverine_outgoing_envelopes
                FOR EACH ROW EXECUTE FUNCTION reject_terminal_webhook();
                """);
            try
            {
                var writer = publisher.Services.GetRequiredService<IMediaTaskTerminalWriter>();
                await Assert.ThrowsAnyAsync<Exception>(() => writer.CommitAsync(new(id, TaskState.Completed, Worker,
                    Webhook: new() { TaskId = id, WebhookUrl = "https://receiver.test/callback" })));
                await using var db = fixture.Db();
                Assert.Equal((int)TaskState.Processing, (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
                Assert.Empty(await db.WebhookDeliveries.ToListAsync());
            }
            finally { await fixture.SqlAsync("DROP TRIGGER reject_terminal_webhook ON wolverine_media_test.wolverine_outgoing_envelopes; DROP FUNCTION reject_terminal_webhook()"); }
        }
        finally { await publisher.StopAsync(); }
    }

    [Fact]
    public async Task LateProgressAndStaleWorker_CannotReopenOrOverwriteTerminalOutcome()
    {
        await fixture.ResetAsync();
        using var host = fixture.Host(worker: false);
        await host.StartAsync();
        try
        {
            var id = await AcceptAndClaimAsync(host, "image", "");
            var writer = host.Services.GetRequiredService<IMediaTaskTerminalWriter>();
            Assert.False(await writer.CommitAsync(new(id, TaskState.Completed, "stale-worker")));
            Assert.True(await writer.CommitAsync(new(id, TaskState.Completed, Worker, Progress: 100, ResultJson: "{\"data\":[]}")));
            var tasks = new HybridAsyncTaskService(new ConduitLLM.Configuration.Repositories.EfAsyncTaskRuntimeStore(fixture.Repository()), fixture.Cache.Object,
                host.Services.GetRequiredService<IEventBus>(), NullLogger<HybridAsyncTaskService>.Instance, writer);
            await tasks.UpdateTaskStatusAsync(id, TaskState.Processing, progress: 42);
            await using var db = fixture.Db();
            var task = await db.AsyncTasks.SingleAsync(t => t.Id == id);
            Assert.Equal((int)TaskState.Completed, task.State);
            Assert.Equal(100, task.Progress);
            Assert.Equal("{\"data\":[]}", task.Result);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task ApiCancellationAfterProviderCompletion_CommitsCallbackBeforeClearingLease()
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync();
        using var host = fixture.Host(worker: false, webhooks: true);
        await host.StartAsync();
        try
        {
            var id = await AcceptAndClaimAsync(host, "video", receiver.Url);
            var writer = host.Services.GetRequiredService<IMediaTaskTerminalWriter>();
            Assert.True(await writer.CommitAsync(new(id, TaskState.Cancelled, Error: "Task was cancelled")));
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(receiver.Posts.Count == 1));
            var post = Assert.Single(receiver.Posts);
            Assert.Contains("cancelled", post.Json);
            Assert.Equal("Bearer terminal-test", post.Authorization);
            Assert.False(await writer.CommitAsync(new(id, TaskState.Completed, Worker)));
            await using var db = fixture.Db();
            Assert.Equal((int)TaskState.Cancelled, (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
        }
        finally { await host.StopAsync(); }
    }

    private async Task<string> AcceptAndClaimAsync(IHost host, string type, string url)
    {
        var metadata = new TaskMetadata(1) { Model = "test-model", WebhookUrl = url,
            WebhookHeaders = new() { ["Authorization"] = "Bearer terminal-test" },
            ExtensionData = new() { ["VirtualKey"] = "test-key" } };
        var submission = host.Services.GetRequiredService<IMediaTaskSubmission>();
        var id = type == "image"
            ? await submission.SubmitAsync(new ImageGenerationRequested { VirtualKeyId = 1,
                Request = new() { Model = "test-model", Prompt = "test" }, WebhookUrl = url, WebhookHeaders = metadata.WebhookHeaders }, metadata)
            : await submission.SubmitAsync(new VideoGenerationRequested { VirtualKeyId = "1", IsAsync = true,
                Request = new() { Model = "test-model", Prompt = "test" }, WebhookUrl = url, WebhookHeaders = metadata.WebhookHeaders }, metadata);
        var repository = fixture.Repository();
        await repository.TryClaimTaskAsync(id, Worker, TimeSpan.FromMinutes(15));
        Assert.True(await repository.MarkProviderInvocationStartedAsync(id, Worker));
        Assert.True(await repository.MarkProviderInvocationCompletedAsync(id, Worker));
        return id;
    }
}
