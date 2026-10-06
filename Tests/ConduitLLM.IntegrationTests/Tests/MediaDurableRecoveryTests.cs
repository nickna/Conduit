using System.Text.Json;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using Wolverine;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "MediaDispatch")]
public sealed class MediaDurableRecoveryTests(MediaDispatchFixture fixture)
{
    private async Task<string> ConsumedClaimAsync(bool postProvider = false)
    {
        var (publisher, id) = await fixture.StartPublisherAsync("image");
        using (publisher) { publisher.Kill(entireProcessTree: true); await publisher.WaitForExitAsync(); }
        using var claimant = fixture.StartProbe("claim", id, postProvider ? "post-provider" : "pre-provider");
        try
        {
            Assert.Equal("CLAIMED:Claimed", await claimant.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally { if (!claimant.HasExited) claimant.Kill(entireProcessTree: true); await claimant.WaitForExitAsync(); }
        using var worker = fixture.Host(worker: true);
        await worker.StartAsync();
        try
        {
            // This is the real Wolverine early-redelivery/normal-return boundary.
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(Volatile.Read(ref fixture.HandledImages) > 0));
            fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { await worker.StopAsync(); }
        return id;
    }

    private Task ExpireAsync(string id) => fixture.SqlAsync($"UPDATE \"AsyncTasks\" SET \"LeaseExpiryTime\" = now() - interval '1 minute' WHERE \"Id\" = '{id}'");
    private async Task<MediaTaskRecoveryResult> RecoverAsync()
    {
        using var host = fixture.Host(worker: false);
        await host.StartAsync();
        try { return await host.Services.GetRequiredService<IMediaTaskRecovery>().RecoverAsync(); }
        finally { await host.StopAsync(); }
    }
    private async Task AssertGeneratedOnceAsync(string id, bool twoHosts = false)
    {
        using var first = fixture.Host(worker: true);
        using var second = twoHosts ? fixture.Host(worker: true) : null;
        await first.StartAsync();
        if (second != null) await second.StartAsync();
        try
        {
            await using var db = fixture.Db();
            var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
            var metadata = JsonSerializer.Deserialize(row.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!;
            var command = ConduitLLM.Core.Utilities.MediaGenerationCommandReconstruction.Build(id, row.Type, metadata).Image!;
            await first.Services.GetRequiredService<IMessageBus>().PublishAsync(command);
            if (second != null) await second.Services.GetRequiredService<IMessageBus>().PublishAsync(command);
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
        finally { await first.StopAsync(); if (second != null) await second.StopAsync(); }
    }

    [Fact]
    public async Task KilledClaimant_EarlyRedeliveryAcknowledged_ConcurrentSweepsAndHostsResumeOnce()
    {
        await fixture.ResetAsync();
        var id = await ConsumedClaimAsync();
        await ExpireAsync(id);
        var results = await Task.WhenAll(RecoverAsync(), RecoverAsync());
        Assert.Equal(1, results.Sum(r => r.ResetToPending));
        Assert.Equal(1, results.Sum(r => r.Redispatched));
        await AssertGeneratedOnceAsync(id, twoHosts: true);
    }

    [Fact]
    public async Task RecoveryProcessKilledBetweenResetAndOutboxWrite_RollsBackAndRestartResumesOnce()
    {
        await fixture.ResetAsync();
        var id = await ConsumedClaimAsync();
        await ExpireAsync(id);
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(14170001)", blocker)) await hold.ExecuteNonQueryAsync();
        await fixture.SqlAsync("""
            CREATE OR REPLACE FUNCTION pause_recovery_outbox() RETURNS trigger AS $$
            BEGIN PERFORM pg_advisory_xact_lock(14170001); RETURN NEW; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER pause_recovery_outbox BEFORE INSERT ON wolverine_media_test.wolverine_outgoing_envelopes
                FOR EACH ROW EXECUTE FUNCTION pause_recovery_outbox();
            """);
        using var recovery = fixture.StartProbe("recover");
        try
        {
            await MediaDispatchFixture.EventuallyAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(fixture.ConnectionString);
                await connection.OpenAsync();
                await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name = 'media-dispatch-probe' AND wait_event = 'advisory')", connection);
                return (bool)(await query.ExecuteScalarAsync())!;
            });
            recovery.Kill(entireProcessTree: true); await recovery.WaitForExitAsync();
        }
        finally
        {
            if (!recovery.HasExited) { recovery.Kill(entireProcessTree: true); await recovery.WaitForExitAsync(); }
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(14170001)", blocker)) await release.ExecuteNonQueryAsync();
            await fixture.SqlAsync("DROP TRIGGER pause_recovery_outbox ON wolverine_media_test.wolverine_outgoing_envelopes; DROP FUNCTION pause_recovery_outbox()");
        }
        await using (var db = fixture.Db()) Assert.Equal(1, (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
        Assert.Equal(1, (await RecoverAsync()).Redispatched);
        await AssertGeneratedOnceAsync(id);
    }

    [Fact]
    public async Task PostProviderMarkerCrash_RecoveryRemainsIndeterminateWithNoAutomaticCallOrDebit()
    {
        await fixture.ResetAsync();
        var id = await ConsumedClaimAsync(postProvider: true);
        await ExpireAsync(id);
        var result = await RecoverAsync();
        Assert.Equal(1, result.MarkedIndeterminate); Assert.Equal(0, result.Redispatched);
        using var worker = fixture.Host(worker: true);
        await worker.StartAsync();
        try
        {
            await using var db = fixture.Db();
            var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
            Assert.Equal(6, row.State); Assert.False(row.IsRetryable);
            var command = JsonSerializer.Deserialize(row.Payload!, CoreMessagingJsonContext.Default.ImageGenerationRequested)!;
            var seen = fixture.HandledImages;
            await worker.Services.GetRequiredService<IMessageBus>().PublishAsync(command);
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(fixture.HandledImages > seen));
            fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
        }
        finally { await worker.StopAsync(); }
    }

    [Fact]
    public async Task HistoricalBacklog_IsBoundedToOneHundredPerPass_AndNextPassMakesProgress()
    {
        await fixture.ResetAsync();
        var template = await AddStrandedAsync();
        await using (var db = fixture.Db())
        {
            var original = await db.AsyncTasks.SingleAsync(t => t.Id == template);
            for (var index = 0; index < 100; index++)
                db.AsyncTasks.Add(new AsyncTask { Id = $"task_bounded_{index}", Type = original.Type,
                    VirtualKeyId = 1, Metadata = original.Metadata });
            await db.SaveChangesAsync();
            await db.AsyncTasks.ExecuteUpdateAsync(setters => setters.SetProperty(t => t.UpdatedAt, DateTime.UtcNow.AddMinutes(-10)));
        }
        Assert.Equal(100, (await RecoverAsync()).Redispatched);
        Assert.Equal(1, (await RecoverAsync()).Redispatched);
        Assert.Equal(0, (await RecoverAsync()).Redispatched);
    }

    [Fact]
    public async Task HistoricalPending_MalformedAndTerminalRows_OnlyEligibleWorkRuns()
    {
        await fixture.ResetAsync();
        var id = await AddStrandedAsync();
        var malformed = await AddStrandedAsync(metadata: "not-json");
        var incomplete = await AddStrandedAsync(metadata: JsonSerializer.Serialize(new TaskMetadata(1), AsyncTaskJsonContext.Default.TaskMetadata));
        var cancelled = await AddStrandedAsync(state: 4);
        var completed = await AddStrandedAsync(state: 2);
        var archived = await AddStrandedAsync(archived: true);
        var result = await RecoverAsync();
        Assert.Equal(1, result.Redispatched); Assert.Equal(2, result.Blocked);
        await using var db = fixture.Db();
        foreach (var bad in new[] { malformed, incomplete })
        {
            var row = await db.AsyncTasks.SingleAsync(t => t.Id == bad);
            Assert.Equal(0, row.State); Assert.StartsWith("Dispatch recovery blocked:", row.Error);
        }
        Assert.Equal(4, (await db.AsyncTasks.SingleAsync(t => t.Id == cancelled)).State);
        Assert.Equal(2, (await db.AsyncTasks.SingleAsync(t => t.Id == completed)).State);
        Assert.True((await db.AsyncTasks.SingleAsync(t => t.Id == archived)).IsArchived);
        await AssertGeneratedOnceAsync(id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleWorkerCannotInvokeProviderOrOverwriteReplacementCompletion(bool validationFails)
    {
        await fixture.ResetAsync();
        var id = await AddStrandedAsync();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        fixture.BeforeKeyValidation = async () =>
        {
            if (Interlocked.Increment(ref validations) == 1)
            {
                paused.TrySetResult(); await resume.Task;
                if (validationFails) throw new IOException("Old owner's validation failed after replacement completed.");
            }
        };
        using var first = fixture.Host(worker: true);
        using var second = fixture.Host(worker: true);
        await first.StartAsync(); await second.StartAsync();
        try
        {
            await RecoverAsync();
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await ExpireAsync(id);
            Assert.Equal(1, (await RecoverAsync()).Redispatched);
            await MediaDispatchFixture.EventuallyAsync(async () =>
            {
                await using var db = fixture.Db();
                return await db.VirtualKeyGroupTransactions.AnyAsync(t => t.IdempotencyKey == $"spend:{id}");
            });
            resume.TrySetResult();
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(fixture.HandledImages >= 2));
            fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            await using var db = fixture.Db();
            Assert.Equal(2, (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State);
            Assert.Equal(99.99m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
        }
        finally { resume.TrySetResult(); await first.StopAsync(); await second.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedCancellationBeforeWorkerUnwinds_PreservesProviderUncertainty(bool providerStarted)
    {
        await fixture.ResetAsync();
        var id = await AddStrandedAsync();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (providerStarted)
            fixture.Provider.Setup(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (ImageGenerationRequest _, string _, CancellationToken cancellation) =>
                {
                    paused.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellation);
                    return new ImageGenerationResponse { Created = 1, Data = [] };
                });
        else
        {
            var reservations = new Mock<IBatchSpendUpdateService>();
            reservations.Setup(r => r.TryReserveSpendAsync(1, It.IsAny<decimal>(), id))
                .Returns(async () => { paused.TrySetResult(); await resume.Task; return true; });
            reservations.Setup(r => r.ReleaseSpendReservationAsync(1, id)).Returns(Task.CompletedTask);
            fixture.Reservations = reservations.Object;
        }

        using var worker = fixture.Host(worker: true);
        await worker.StartAsync();
        try
        {
            await RecoverAsync();
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await worker.Services.GetRequiredService<IAsyncTaskService>().CancelTaskAsync(id);
            Assert.True(worker.Services.GetRequiredService<ICancellableTaskRegistry>().TryCancel(id));
            resume.TrySetResult();
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(fixture.HandledImages >= 1));
            await using var db = fixture.Db();
            var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
            Assert.Equal(providerStarted ? 6 : 4, row.State);
            if (providerStarted) Assert.False(row.IsRetryable);
            Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
            Assert.Equal(0, (await RecoverAsync()).Redispatched);
            fixture.Provider.Verify(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                providerStarted ? Times.Once() : Times.Never());
            if (!providerStarted)
                Mock.Get(fixture.Reservations!).Verify(r => r.ReleaseSpendReservationAsync(1, id), Times.Once);
        }
        finally { resume.TrySetResult(); await worker.StopAsync(); }
    }

    private async Task<string> AddStrandedAsync(int state = 0, bool archived = false, string? metadata = null)
    {
        var id = $"task_{Guid.NewGuid():N}";
        var request = new ImageGenerationRequested { VirtualKeyId = 1, Request = new() { Model = "test-model", Prompt = "legacy complete request" } };
        var original = new TaskMetadata(1)
        {
            Payload = JsonSerializer.Serialize(request, CoreMessagingJsonContext.Default.ImageGenerationRequested),
            ExtensionData = new() { ["VirtualKey"] = "test-key" }
        };
        await fixture.Repository().CreateAsync(new AsyncTask
        {
            Id = id, Type = "image_generation", State = state, IsArchived = archived, VirtualKeyId = 1,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10), UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
            Metadata = metadata ?? JsonSerializer.Serialize(original, AsyncTaskJsonContext.Default.TaskMetadata)
        });
        // Repository auditing intentionally stamps new rows with the current time.
        await fixture.SqlAsync($"UPDATE \"AsyncTasks\" SET \"UpdatedAt\" = now() - interval '10 minutes' WHERE \"Id\" = '{id}'");
        return id;
    }
}
