using System.Text.Json;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wolverine;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "MediaDispatch")]
public sealed class MediaDurableAcceptanceTests(MediaDispatchFixture fixture)
{
    private static TaskMetadata Metadata() => new(1)
    {
        Model = "test-model", Prompt = "complete prompt", ExtensionData = new() { ["VirtualKey"] = "test-key" }
    };

    [Fact]
    public async Task KilledPublisher_RestartedWolverine_GeneratesAndDebitsOnceDespiteDuplicateCommand()
    {
        await fixture.ResetAsync();
        var (publisher, id) = await fixture.StartPublisherAsync("image");
        using (publisher) { publisher.Kill(entireProcessTree: true); await publisher.WaitForExitAsync(); }
        await using var db = fixture.Db();
        var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
        var command = JsonSerializer.Deserialize(row.Payload!, CoreMessagingJsonContext.Default.ImageGenerationRequested)!;
        Assert.Equal(id, command.TaskId);
        Assert.Equal("edit-mask", command.Request.Mask);
        Assert.Equal("test-user", command.UserId);
        using var worker = fixture.Host(worker: true);
        await worker.StartAsync();
        try
        {
            await worker.Services.GetRequiredService<IMessageBus>().PublishAsync(command);
            await MediaDispatchFixture.EventuallyAsync(async () =>
            {
                await using var current = fixture.Db();
                return await current.VirtualKeyGroupTransactions.AnyAsync(t => t.IdempotencyKey == $"spend:{id}");
            });
            fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            await using var current = fixture.Db();
            Assert.Equal(99.99m, (await current.VirtualKeyGroups.SingleAsync()).Balance);
            Assert.Single(await current.VirtualKeyGroupTransactions.ToListAsync());
            Assert.Equal(2, (await current.AsyncTasks.SingleAsync(t => t.Id == id)).State);
        }
        finally { await worker.StopAsync(); }
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    public async Task TransportPublishFailure_AcceptedTaskSurvivesProcessRestart(string type)
    {
        await fixture.ResetAsync();
        await fixture.SqlAsync("""
            CREATE OR REPLACE FUNCTION reject_media_transport() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected transport publication failure'; END; $$ LANGUAGE plpgsql;
            DO $$ DECLARE row record; BEGIN
            FOR row IN SELECT tablename FROM pg_tables WHERE schemaname = 'wolverine_queues' LOOP
              EXECUTE format('CREATE TRIGGER reject_media_transport BEFORE INSERT ON wolverine_queues.%I FOR EACH ROW EXECUTE FUNCTION reject_media_transport()', row.tablename);
            END LOOP; END $$;
            """);
        string id;
        try
        {
            var (publisher, acceptedId) = await fixture.StartPublisherAsync(type);
            id = acceptedId;
            using (publisher) { publisher.Kill(entireProcessTree: true); await publisher.WaitForExitAsync(); }
            await using var db = fixture.Db();
            Assert.Equal(0, (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
        }
        finally
        {
            await fixture.SqlAsync("""
                DO $$ DECLARE row record; BEGIN
                FOR row IN SELECT tablename FROM pg_tables WHERE schemaname = 'wolverine_queues' LOOP
                  EXECUTE format('DROP TRIGGER reject_media_transport ON wolverine_queues.%I', row.tablename);
                END LOOP; END $$;
                DROP FUNCTION reject_media_transport();
                """);
        }
        // The killed publisher's node can still look live during the first
        // leadership assignment and consume a 60s remote-command timeout. Advance
        // only its heartbeat in this isolated database, as if that timeout elapsed.
        // The outgoing envelopes themselves must be recovered by Wolverine.
        await fixture.SqlAsync("UPDATE wolverine_media_test.wolverine_nodes SET health_check = CURRENT_TIMESTAMP - INTERVAL '10 minutes'");
        using var worker = fixture.Host(worker: true);
        await worker.StartAsync();
        try
        {
            await MediaDispatchFixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Db();
                return await db.VirtualKeyGroupTransactions.AnyAsync(t => t.IdempotencyKey == $"spend:{id}");
            });
            if (type == "image")
                fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            else
                fixture.Provider.As<IVideoGenerationClient>().Verify(p => p.CreateVideoAsync(
                    It.Is<VideoGenerationRequest>(r => r.Duration == 7), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            await using var db = fixture.Db();
            Assert.Equal(99.99m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
        }
        finally { await worker.StopAsync(); }
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    public async Task OutboxWriteFailure_RollsBackRunnableTask(string type)
    {
        await fixture.ResetAsync();
        using var host = fixture.Host(worker: false);
        await host.StartAsync();
        await fixture.SqlAsync("""
            CREATE OR REPLACE FUNCTION reject_media_outbox() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected outbox persistence failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_media_outbox BEFORE INSERT ON wolverine_media_test.wolverine_outgoing_envelopes
                FOR EACH ROW EXECUTE FUNCTION reject_media_outbox();
            """);
        try
        {
            var submission = host.Services.GetRequiredService<IMediaTaskSubmission>();
            if (type == "image")
                await Assert.ThrowsAnyAsync<Exception>(() => submission.SubmitAsync(new ImageGenerationRequested
                    { VirtualKeyId = 1, Request = new() { Prompt = "test", Model = "test-model" } }, Metadata()));
            else
                await Assert.ThrowsAnyAsync<Exception>(() => submission.SubmitAsync(new VideoGenerationRequested
                    { VirtualKeyId = "1", IsAsync = true, Request = new() { Prompt = "test", Model = "test-model" } }, Metadata()));
            await using var db = fixture.Db();
            Assert.Empty(await db.AsyncTasks.ToListAsync());
        }
        finally
        {
            await fixture.SqlAsync("DROP TRIGGER reject_media_outbox ON wolverine_media_test.wolverine_outgoing_envelopes; DROP FUNCTION reject_media_outbox()");
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CacheAndNotificationOutage_AcceptanceRemainsDurableAndReadable()
    {
        await fixture.ResetAsync();
        fixture.Cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("cache unavailable"));
        using var host = fixture.Host(worker: false, notificationsFail: true);
        await host.StartAsync();
        try
        {
            var id = await host.Services.GetRequiredService<IMediaTaskSubmission>().SubmitAsync(new ImageGenerationRequested
                { VirtualKeyId = 1, Request = new() { Prompt = "test", Model = "test-model" } }, Metadata());
            var taskService = new ConduitLLM.Core.Services.HybridAsyncTaskService(new ConduitLLM.Configuration.Repositories.EfAsyncTaskRuntimeStore(fixture.Repository()), fixture.Cache.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ConduitLLM.Core.Services.HybridAsyncTaskService>.Instance);
            var status = await taskService.GetTaskStatusAsync(id);
            Assert.Equal(id, status!.TaskId);
            Assert.NotEmpty(status.Metadata!.Payload!);
        }
        finally { await host.StopAsync(); }
    }
}
