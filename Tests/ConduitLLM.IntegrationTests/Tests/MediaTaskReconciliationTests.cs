using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Utilities;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Durable media dispatch")]
[Trait("Category", "Integration")]
[Trait("Component", "MediaDispatch")]
public sealed class MediaTaskReconciliationTests(MediaDispatchFixture fixture)
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aYQAAAAASUVORK5CYII=");
    private static readonly byte[] Video = [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x6d, 0, 0, 0, 0];

    [Theory]
    [InlineData("image")] [InlineData("video")]
    public async Task AdminHttp_AuthenticatesAndValidatesEvidence_RecoversOriginalResultWithoutGenerationOrDebit(string type)
    {
        await fixture.ResetAsync(); ConfigureMedia(type);
        await using var receiver = await WebhookReceiver.StartAsync();
        using var worker = fixture.Host(worker: false, webhooks: true, reconciliation: true);
        await worker.StartAsync();
        try
        {
            var id = await SeedAsync(worker, type, receiver.Url);
            using var admin = new AdminFactory(fixture);
            using var client = admin.CreateClient();
            var request = Dto(Decision());
            request.ResultUrls = ["https://media.provider.example/original?signature=private-token"];
            var path = $"/v1/admin/tasks/{id}/resolve";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, request)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminFactory.MasterKey);
            request.VirtualKeyId = 2;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, request)).StatusCode);
            request.VirtualKeyId = 1; request.ProviderId = 2;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, request)).StatusCode);
            request.ProviderId = 1; request.ProviderOperationId = "different-operation";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, request)).StatusCode);
            request.ProviderOperationId = "original-operation";
            var response = await client.PostAsJsonAsync(path, request);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            await CompletedAsync(id);
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(receiver.Posts.Count == 1));
            await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(fixture.RecoveryNotifications == 1));
            await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db(); return await db.MediaRecords.AnyAsync(); });
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(path, request)).StatusCode);
            await worker.Services.GetRequiredService<IEventBus>().PublishAsync(new IndeterminateMediaTaskRecoveryRequested
                { TaskId = id, DispatchId = request.DispatchId!.Value.ToString("N") });
            await using var db = fixture.Db();
            var task = await db.AsyncTasks.SingleAsync(t => t.Id == id);
            var result = JsonSerializer.Deserialize(task.Result!, AsyncTaskJsonContext.Default.MediaGenerationTaskResult)!;
            Assert.Equal("test-model", result.Model); Assert.Equal("https://stored.example/recovered", Assert.Single(result.Data).Url);
            Assert.DoesNotContain("private-token", task.Result);
            if (type == "image")
                fixture.RecoveryStorage.Verify(x => x.StoreAsync(It.IsAny<Stream>(),
                    It.Is<MediaMetadata>(m => !m.CustomMetadata.ContainsKey("originalUrl")), It.IsAny<IProgress<long>>()), Times.Once);
            else
                fixture.RecoveryStorage.Verify(x => x.StoreVideoAsync(It.IsAny<Stream>(),
                    It.Is<VideoMediaMetadata>(m => !m.CustomMetadata.ContainsKey("originalUrl")), It.IsAny<Action<long>>()), Times.Once);
            Assert.Equal(0, result.InternalMetadata.Cost); Assert.Equal("original-operation", task.ProviderOperationId);
            var audit = JsonSerializer.Deserialize(task.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!.Reconciliation!;
            Assert.Equal("completed", audit.Outcome); Assert.Equal("no_customer_charge", audit.BillingDisposition);
            Assert.Equal(0.75m, audit.ProviderCost); Assert.Equal("incident-1328", audit.Evidence);
            Assert.False(task.IsRetryable); Assert.Null(task.LeasedBy);
            Assert.Empty(fixture.Provider.Invocations); Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
            Assert.Equal(100, (await db.VirtualKeyGroups.SingleAsync()).Balance);
            var media = Assert.Single(await db.MediaRecords.ToListAsync());
            Assert.Equal(1, media.VirtualKeyId); Assert.Equal("recovered", media.StorageKey);
            Assert.Equal(type == "image" ? "image/png" : "video/mp4", media.ContentType);
            Assert.Equal(type == "image" ? Png.Length : Video.Length, media.SizeBytes);
            Assert.Contains("https://stored.example/recovered", Assert.Single(receiver.Posts).Json);
            Assert.DoesNotContain("private-token", Assert.Single(receiver.Posts).Json);
        }
        finally { await worker.StopAsync(); }
    }

    [Fact]
    public async Task AcceptedRecovery_ReservesAgainstCompetingDecisions_ResumesAfterPublisherStops()
    {
        await fixture.ResetAsync(); ConfigureMedia("image");
        string id; var decision = Decision();
        using (var publisher = fixture.Host(worker: false))
        {
            await publisher.StartAsync();
            try
            {
                id = await SeedAsync(publisher, "image");
                var store = publisher.Services.GetRequiredService<IMediaTaskReconciliationStore>();
                var accepted = await Task.WhenAll(store.AcceptAsync(id, decision), store.AcceptAsync(id, decision));
                Assert.Contains(MediaReconciliationAcceptance.Accepted, accepted);
                Assert.Contains(MediaReconciliationAcceptance.AlreadyAccepted, accepted);
                Assert.Equal(MediaReconciliationAcceptance.Conflict, await store.AcceptAsync(id, Decision()));
                Assert.False(await fixture.Repository().FailIndeterminateTaskWithoutChargeAsync(id, "competing failure"));
                Assert.Equal(ConduitLLM.Configuration.Interfaces.IndeterminateTaskRetryPreparationStatus.NotIndeterminate,
                    (await fixture.Repository().PrepareIndeterminateTaskRetryAsync(id, "competing-retry", "competing retry")).Status);
                await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
                var native = new ConduitLLM.Persistence.Npgsql.NpgsqlAsyncTaskRuntimeStore(source);
                Assert.False(await native.FailIndeterminateTaskWithoutChargeAsync(id, "competing failure"));
                Assert.Equal(ConduitLLM.Persistence.AsyncTaskRuntimeRetryStatus.NotIndeterminate,
                    (await native.PrepareIndeterminateTaskRetryAsync(id, "native-retry", "competing retry")).Status);
            }
            finally { await publisher.StopAsync(); }
        }
        using var restarted = fixture.Host(worker: false, reconciliation: true);
        await restarted.StartAsync();
        try { await CompletedAsync(id); Assert.Empty(fixture.Provider.Invocations); }
        finally { await restarted.StopAsync(); }
    }

    [Fact]
    public async Task AcceptanceOutboxFailure_RollsBackReservationAndEvidence()
    {
        await fixture.ResetAsync();
        using var publisher = fixture.Host(worker: false); await publisher.StartAsync();
        try
        {
            var id = await SeedAsync(publisher, "image");
            await fixture.SqlAsync("""
                CREATE FUNCTION reject_reconciliation() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'reconciliation outbox fault'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_reconciliation BEFORE INSERT ON wolverine_media_test.wolverine_outgoing_envelopes FOR EACH ROW EXECUTE FUNCTION reject_reconciliation();
                """);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => publisher.Services.GetRequiredService<IMediaTaskReconciliationStore>().AcceptAsync(id, Decision()));
                await using var db = fixture.Db(); var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
                Assert.Equal(6, row.State); Assert.Null(row.LeasedBy);
                Assert.Null(JsonSerializer.Deserialize(row.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!.Reconciliation);
            }
            finally { await fixture.SqlAsync("DROP TRIGGER reject_reconciliation ON wolverine_media_test.wolverine_outgoing_envelopes; DROP FUNCTION reject_reconciliation()"); }
        }
        finally { await publisher.StopAsync(); }
    }

    [Fact]
    public async Task HistoricalMissingProviderIdentity_RequiresOperatorEvidenceAndPreservesOriginalTask()
    {
        await fixture.ResetAsync(); ConfigureMedia("image");
        using var worker = fixture.Host(worker: false, reconciliation: true); await worker.StartAsync();
        try
        {
            var id = await SeedAsync(worker, "image");
            await using (var db = fixture.Db())
            {
                var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
                var metadata = JsonSerializer.Deserialize(row.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!;
                metadata.MediaProvider = null; metadata.ProviderInvocationWorkerId = null;
                row.Metadata = JsonSerializer.Serialize(metadata, AsyncTaskJsonContext.Default.TaskMetadata);
                row.ProviderOperationId = null; row.LeasedBy = "legacy-expired-worker"; row.LeaseExpiryTime = DateTime.UtcNow.AddMinutes(-5);
                await db.SaveChangesAsync();
            }
            var store = worker.Services.GetRequiredService<IMediaTaskReconciliationStore>();
            Assert.Equal(MediaReconciliationAcceptance.Invalid, await store.AcceptAsync(id, Decision() with { Evidence = "" }));
            Assert.Equal(MediaReconciliationAcceptance.Accepted, await store.AcceptAsync(id, Decision()));
            await CompletedAsync(id);
            await using var verified = fixture.Db(); var task = await verified.AsyncTasks.SingleAsync(t => t.Id == id);
            Assert.Equal("original-operation", task.ProviderOperationId);
            Assert.Empty(fixture.Provider.Invocations); Assert.Empty(await verified.VirtualKeyGroupTransactions.ToListAsync());
        }
        finally { await worker.StopAsync(); }
    }

    [Fact]
    public async Task ExpiredConfirmedOutput_CanCloseAsAuditedDeliveryFailureWithoutCharge()
    {
        await fixture.ResetAsync(); ConfigureMedia("image", 404, []);
        using var worker = fixture.Host(worker: false, reconciliation: true); await worker.StartAsync();
        try
        {
            var id = await SeedAsync(worker, "image");
            using var admin = new AdminFactory(fixture); using var client = admin.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminFactory.MasterKey);
            var path = $"/v1/admin/tasks/{id}/resolve";
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(path, Dto(Decision()))).StatusCode);
            await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db();
                return (await db.AsyncTasks.SingleAsync(t => t.Id == id)).Error?.StartsWith("Recovery blocked:") == true; });
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(path, new ResolveIndeterminateTaskDto
            { Resolution = "failed_no_charge", Reason = "Provider confirmed success; original output expired; delivery failed and provider cost written off" })).StatusCode);
            await using var final = fixture.Db(); var task = await final.AsyncTasks.SingleAsync(t => t.Id == id);
            Assert.Equal(3, task.State); Assert.False(task.IsRetryable); Assert.Null(task.Result);
            Assert.Contains("Provider confirmed success", task.Error);
            var decision = JsonSerializer.Deserialize(task.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!.Reconciliation!;
            Assert.Equal("blocked", decision.Outcome); Assert.Equal(0.75m, decision.ProviderCost);
            Assert.Empty(fixture.Provider.Invocations); Assert.Empty(await final.VirtualKeyGroupTransactions.ToListAsync());
            Assert.Equal(100, (await final.VirtualKeyGroups.SingleAsync()).Balance);
        }
        finally { await worker.StopAsync(); }
    }

    [Theory]
    [InlineData(200, "malformed")] [InlineData(404, "missing")] [InlineData(302, "redirect")]
    public async Task UnusableOutput_StaysIndeterminate_ExplicitFreshDecisionCanRecover(int status, string body)
    {
        await fixture.ResetAsync(); ConfigureMedia("image", status, System.Text.Encoding.UTF8.GetBytes(body));
        using var worker = fixture.Host(worker: false, reconciliation: true); await worker.StartAsync();
        try
        {
            var id = await SeedAsync(worker, "image"); var decision = Decision();
            var store = worker.Services.GetRequiredService<IMediaTaskReconciliationStore>();
            Assert.Equal(MediaReconciliationAcceptance.Accepted, await store.AcceptAsync(id, decision));
            await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db(); return (await db.AsyncTasks.SingleAsync(t => t.Id == id)).Error?.StartsWith("Recovery blocked:") == true; });
            await using (var db = fixture.Db())
            {
                var row = await db.AsyncTasks.SingleAsync(t => t.Id == id);
                Assert.Equal(6, row.State); Assert.Null(row.Result); Assert.Null(row.LeasedBy);
            }
            Assert.Null(await store.GetAsync(id, decision.DispatchId));
            ConfigureMedia("image");
            Assert.Equal(MediaReconciliationAcceptance.Accepted, await store.AcceptAsync(id, Decision()));
            await CompletedAsync(id);
            await using var finalDb = fixture.Db();
            var metadata = JsonSerializer.Deserialize((await finalDb.AsyncTasks.SingleAsync()).Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!;
            Assert.Equal("blocked", Assert.Single(metadata.ReconciliationHistory!).Outcome);
            Assert.Empty(fixture.Provider.Invocations); Assert.Empty(await finalDb.VirtualKeyGroupTransactions.ToListAsync());
        }
        finally { await worker.StopAsync(); }
    }

    [Fact]
    public async Task ProviderAcceptance_IsRecordedBeforeFailureAndNotErasedByCompletion()
    {
        await fixture.ResetAsync(); using var host = fixture.Host(worker: false); await host.StartAsync();
        try
        {
            var id = await SeedAsync(host, "image", indeterminate: false);
            Assert.True(await fixture.Repository().MarkProviderInvocationCompletedAsync(id, "reconciliation-test"));
            await using var db = fixture.Db(); var task = await db.AsyncTasks.SingleAsync(t => t.Id == id);
            Assert.Equal("original-operation", task.ProviderOperationId);
            Assert.Equal("original-operation", JsonSerializer.Deserialize(task.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!.MediaProvider!.OperationId);
        }
        finally { await host.StopAsync(); }
    }

    private async Task CompletedAsync(string id) => await MediaDispatchFixture.EventuallyAsync(async () =>
    { await using var db = fixture.Db(); return (await db.AsyncTasks.SingleAsync(t => t.Id == id)).State == 2; });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task KilledRecoveryProducer_RollsBackOrCommitsResultAuditAndWebhookTogether(bool afterCommit)
    {
        await fixture.ResetAsync(); ConfigureMedia("image");
        await using var receiver = await WebhookReceiver.StartAsync();
        using var publisher = fixture.Host(worker: false); await publisher.StartAsync();
        try
        {
            var id = await SeedAsync(publisher, "image", receiver.Url); var decision = Decision();
            await publisher.Services.GetRequiredService<IMediaTaskReconciliationStore>().AcceptAsync(id, decision);
            if (!afterCommit) await fixture.SqlAsync("""
                CREATE FUNCTION pause_recovery_commit() RETURNS trigger AS $$ BEGIN
                  IF current_setting('application_name') = 'media-dispatch-probe' THEN PERFORM pg_sleep(120); END IF;
                  RETURN NEW; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER pause_recovery_commit BEFORE INSERT ON wolverine_media_test.wolverine_outgoing_envelopes FOR EACH ROW EXECUTE FUNCTION pause_recovery_commit();
                """);
            try
            {
                using var process = fixture.StartProbe("terminal-recovery", id, receiver.Url, decision.DispatchId);
                try
                {
                    Assert.Equal("TERMINAL:starting", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                    if (afterCommit) Assert.Equal("TERMINAL:committed", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                    else await MediaDispatchFixture.EventuallyAsync(async () =>
                    {
                        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
                        await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name = 'media-dispatch-probe' AND wait_event = 'PgSleep')", connection);
                        return (bool)(await query.ExecuteScalarAsync())!;
                    });
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await fixture.SqlAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'media-dispatch-probe'");
                }
            }
            finally
            {
                if (!afterCommit) await fixture.SqlAsync("DROP TRIGGER pause_recovery_commit ON wolverine_media_test.wolverine_outgoing_envelopes; DROP FUNCTION pause_recovery_commit()");
            }
            await using (var db = fixture.Db())
            {
                var task = await db.AsyncTasks.SingleAsync(t => t.Id == id);
                Assert.Equal(afterCommit ? 2 : 6, task.State);
                Assert.Equal(afterCommit ? "completed" : "accepted", JsonSerializer.Deserialize(task.Metadata!, AsyncTaskJsonContext.Default.TaskMetadata)!.Reconciliation!.Outcome);
                Assert.Equal(afterCommit, task.Result != null);
            }
            using var restarted = fixture.Host(worker: false, webhooks: true, reconciliation: true); await restarted.StartAsync();
            try
            {
                await CompletedAsync(id);
                await MediaDispatchFixture.EventuallyAsync(() => Task.FromResult(receiver.Posts.Count == 1));
                Assert.Empty(fixture.Provider.Invocations);
                await using var db = fixture.Db(); Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
            }
            finally { await restarted.StopAsync(); }
        }
        finally { await publisher.StopAsync(); }
    }

    private async Task<string> SeedAsync(Microsoft.Extensions.Hosting.IHost host, string type, string? webhook = null, bool indeterminate = true)
    {
        var metadata = new TaskMetadata(1) { Model = "test-model", WebhookUrl = webhook };
        var submit = host.Services.GetRequiredService<IMediaTaskSubmission>();
        var id = type == "image" ? await submit.SubmitAsync(new ImageGenerationRequested { VirtualKeyId = 1,
            Request = new() { Model = "test-model", Prompt = "recover original" }, WebhookUrl = webhook }, metadata) :
            await submit.SubmitAsync(new VideoGenerationRequested { VirtualKeyId = "1", IsAsync = true,
                Request = new() { Model = "test-model", Prompt = "recover original" }, WebhookUrl = webhook }, metadata);
        await fixture.Repository().TryClaimTaskAsync(id, "reconciliation-test", TimeSpan.FromMinutes(15));
        await fixture.Repository().MarkProviderInvocationStartedAsync(id, "reconciliation-test");
        var store = host.Services.GetRequiredService<IMediaTaskReconciliationStore>();
        await store.RecordInvocationAsync(id, "reconciliation-test", new(1, 0, "provider-model", "Test"));
        await store.RecordOperationAsync(id, "reconciliation-test", new(1, 7, "provider-model", "Test", "original-operation"));
        if (indeterminate)
        {
            await using var db = fixture.Db();
            await db.AsyncTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.State, 6)
                .SetProperty(t => t.LeasedBy, (string?)null).SetProperty(t => t.LeaseExpiryTime, (DateTime?)null));
        }
        return id;
    }

    private static MediaTaskReconciliation Decision() => new() { DispatchId = Guid.NewGuid().ToString("N"),
        VirtualKeyId = 1, ProviderId = 1, Model = "test-model", ProviderModelId = "provider-model", ProviderOperationId = "original-operation",
        Evidence = "incident-1328", Reason = "Provider confirmed the original result belongs to this task", ProviderCost = 0.75m,
        ResultUrls = ["https://media.provider.example/original"] };
    private static ResolveIndeterminateTaskDto Dto(MediaTaskReconciliation decision) => new()
    { Resolution = "completed_no_charge", DispatchId = Guid.ParseExact(decision.DispatchId, "N"), VirtualKeyId = decision.VirtualKeyId,
        ProviderId = decision.ProviderId, ProviderModelId = decision.ProviderModelId, Model = decision.Model,
        ProviderOperationId = decision.ProviderOperationId, Evidence = decision.Evidence, Reason = decision.Reason,
        ProviderCost = decision.ProviderCost, ResultUrls = decision.ResultUrls };

    private void ConfigureMedia(string type, int status = 200, byte[]? body = null)
    {
        fixture.RecoveryClients.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new DownloadHandler(status, body ?? (type == "image" ? Png : Video))));
        fixture.RecoveryStorage.Setup(x => x.StoreAsync(It.IsAny<Stream>(), It.IsAny<MediaMetadata>(), It.IsAny<IProgress<long>>()))
            .ReturnsAsync(new MediaStorageResult { StorageKey = "recovered", Url = "https://stored.example/recovered" });
        fixture.RecoveryStorage.Setup(x => x.StoreVideoAsync(It.IsAny<Stream>(), It.IsAny<VideoMediaMetadata>(), It.IsAny<Action<long>>()))
            .ReturnsAsync(new MediaStorageResult { StorageKey = "recovered", Url = "https://stored.example/recovered" });
    }
    private sealed class DownloadHandler(int status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) });
    }

    private sealed class AdminFactory : WebApplicationFactory<ConduitLLM.Admin.Program>
    {
        public const string MasterKey = "reconciliation-http-master-key";
        private readonly MediaDispatchFixture _fixture;
        private readonly Dictionary<string, string?> _environment = new();
        public AdminFactory(MediaDispatchFixture fixture)
        {
            _fixture = fixture;
            var database = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            Set("DATABASE_URL", $"postgresql://{database.Username}:{database.Password}@{database.Host}:{database.Port}/{database.Database}");
            Set("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", MasterKey); Set("CONDUIT_MIGRATION_MODE", "Skip");
            Set("CONDUIT_OPENAPI_GENERATION", null); Set("REDIS_URL", null); Set("CONDUIT_REDIS_CONNECTION_STRING", null);
            Set("CONDUIT_MEDIA_STORAGE_TYPE", "InMemory"); Set("ConduitLLM__Messaging__Wolverine__Transport", "Postgresql");
            Set("ConduitLLM__Messaging__Wolverine__AutoProvision", "true");
        }
        private void Set(string key, string? value) { _environment[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, value); }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.ConfigureLogging(x => x.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AdminApi:MasterKey"] = MasterKey, [WolverineMessagingExtensions.SchemaNameKey] = "wolverine_admin_reconciliation_test", [WolverineMessagingExtensions.AutoProvisionKey] = "true" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDistributedCache>(); services.AddSingleton(_fixture.Cache.Object);
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
        }
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        }
    }
}
