using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Security.Options;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

using Moq;

namespace ConduitLLM.Tests.Admin.Endpoints;

/// <summary>
/// Exercises the real Admin entry point, generated request delegates, authentication,
/// operation filter, exception middleware, and HTTP JSON contracts. Infrastructure
/// and media-service boundaries are isolated so this suite needs no external server.
/// </summary>
[Collection("MediaRequestDelegateHostEnvironment")]
[Trait("Category", "Integration")]
[Trait("Component", "AdminMediaRequestDelegates")]
public sealed class MediaRequestDelegateHostTests : IDisposable
{
    private readonly MediaRequestDelegateHostFactory _factory = new();
    private readonly HttpClient _client;

    public MediaRequestDelegateHostTests()
    {
        try
        {
            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });
            _client.DefaultRequestHeaders.Add("X-Master-Key", MediaRequestDelegateHostFactory.MasterKey);
        }
        catch
        {
            _factory.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task DeleteAndRestore_BindRouteIdAndPreserveJsonContract()
    {
        var id = MediaRequestDelegateHostFactory.MediaId;
        using var deleted = await _client.DeleteAsync($"/v1/admin/media-assets/{id}");
        using var deleteJson = await ReadJsonAsync(deleted, HttpStatusCode.OK);
        Assert.True(deleteJson.RootElement.GetProperty("isSoftDeleted").GetBoolean());
        Assert.Equal("2026-07-24T01:00:00Z", deleteJson.RootElement.GetProperty("deletedAt").GetString());
        Assert.Contains("restored", deleteJson.RootElement.GetProperty("message").GetString());

        using var restored = await _client.PostAsync($"/v1/admin/media-assets/restore/{id}", null);
        using var restoreJson = await ReadJsonAsync(restored, HttpStatusCode.OK);
        Assert.Equal(id, restoreJson.RootElement.GetProperty("mediaId").GetGuid());
        _factory.Media.Verify(service => service.DeleteMediaAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        _factory.Media.Verify(service => service.RestoreMediaAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(MediaRestoreOutcome.NotDeleted, "media_not_deleted")]
    [InlineData(MediaRestoreOutcome.GracePeriodElapsed, "media_restore_window_elapsed")]
    [InlineData(MediaRestoreOutcome.CleanupInProgress, "media_cleanup_in_progress")]
    public async Task RestoreConflict_PreservesProblemDetails(MediaRestoreOutcome outcome, string code)
    {
        _factory.Media.Setup(service => service.RestoreMediaAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        using var response = await _client.PostAsync(
            $"/v1/admin/media-assets/restore/{MediaRequestDelegateHostFactory.MediaId}", null);
        using var json = await ReadJsonAsync(response, HttpStatusCode.Conflict, "application/problem+json");
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(409, json.RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("restore")]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task MissingResource_UsesRealExceptionMiddleware(string operation)
    {
        _factory.Media.Setup(service => service.DeleteMediaAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminMediaDeleteResult)null);
        _factory.Media.Setup(service => service.RestoreMediaAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MediaRestoreOutcome.NotFound);
        _factory.Approvals.Setup(service => service.ApproveAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaCleanupApproval)null);
        _factory.Approvals.Setup(service => service.RejectAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaCleanupApproval)null);

        using var response = operation switch
        {
            "delete" => await _client.DeleteAsync($"/v1/admin/media-assets/{MediaRequestDelegateHostFactory.MediaId}"),
            "restore" => await _client.PostAsync($"/v1/admin/media-assets/restore/{MediaRequestDelegateHostFactory.MediaId}", null),
            _ => await _client.PostAsync($"/v1/admin/media-cleanup-jobs/approvals/{MediaRequestDelegateHostFactory.ApprovalId}/{operation}", null)
        };
        using var json = await ReadJsonAsync(response, HttpStatusCode.NotFound, "application/problem+json");
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("expired", "expiration")]
    [InlineData("orphaned", "reconciliation")]
    [InlineData("prune", "retention")]
    public async Task Cleanup_BindsForceAndBodyAndReportsManualResult(string route, string cleanupType)
    {
        using var body = route == "prune" ? JsonBody("{\"daysToKeep\":7,\"force\":true}") : null;
        using var response = await _client.PostAsync($"/v1/admin/media-assets/cleanup/{route}?force=true", body);
        using var json = await ReadJsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(2, json.RootElement.GetProperty("deletedCount").GetInt32());
        Assert.Equal("manual", json.RootElement.GetProperty("triggeredBy").GetString());
        _factory.Deletion.Verify(service => service.ExecuteOperationAsync(
                It.Is<MediaDeletionOperationContext>(operation =>
                    operation.CleanupType == cleanupType && operation.Force && operation.TriggeredBy == "manual"),
                It.IsAny<Func<Task<MediaDeletionEngineResult>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _factory.Status.Verify(service => service.RecordRunCompletionAsync(
            2, 64, It.IsAny<double>(), "Completed", It.IsAny<string>(), "manual", It.IsAny<CancellationToken>()), Times.Once);
        if (route != "orphaned")
        {
            _factory.Deletion.Verify(service => service.DeleteAsync(
                It.Is<MediaDeletionRequest>(request => request.MediaRecords.Count == 1 &&
                    request.MediaRecords.Single().Id == MediaRequestDelegateHostFactory.MediaId),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("orphaned")]
    [InlineData("prune")]
    public async Task CleanupContention_ReturnsConflictWithoutExecutingDeletion(string route)
    {
        _factory.Locks.Setup(service => service.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDistributedLockOwnership)null);
        using var body = route == "prune" ? JsonBody("{\"daysToKeep\":7}") : null;
        using var response = await _client.PostAsync($"/v1/admin/media-assets/cleanup/{route}", body);
        using var json = await ReadJsonAsync(response, HttpStatusCode.Conflict, "application/problem+json");
        Assert.Equal("media_cleanup_in_progress", json.RootElement.GetProperty("code").GetString());
        _factory.Deletion.Verify(service => service.ExecuteOperationAsync(
            It.IsAny<MediaDeletionOperationContext>(), It.IsAny<Func<Task<MediaDeletionEngineResult>>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrunePreview_UsesBoundRequestAndPreservesConfirmationContract()
    {
        using var body = JsonBody("{\"daysToKeep\":7}");
        using var response = await _client.PostAsync("/v1/admin/media-assets/cleanup/prune/preview", body);
        using var json = await ReadJsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(1, json.RootElement.GetProperty("fileCount").GetInt32());
        Assert.Equal(32, json.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("DELETE 1", json.RootElement.GetProperty("confirmationPhrase").GetString());
    }

    [Theory]
    [InlineData("prune")]
    [InlineData("prune/preview")]
    public async Task InvalidPruneBody_ReturnsBadRequest(string route)
    {
        using var body = JsonBody("{\"daysToKeep\":0}");
        using var response = await _client.PostAsync($"/v1/admin/media-assets/cleanup/{route}", body);
        using var json = await ReadJsonAsync(response, HttpStatusCode.BadRequest, "application/problem+json");
        Assert.Contains("positive", json.RootElement.GetProperty("detail").GetString());
        _factory.Deletion.Verify(service => service.DeleteAsync(
            It.IsAny<MediaDeletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupSettings_RoundTripThroughGeneratedBodyBindings()
    {
        using var enabledBody = JsonBody("{\"enabled\":false}");
        using var enabledResponse = await _client.PostAsync("/v1/admin/media-cleanup-jobs/enabled", enabledBody);
        using var enabledJson = await ReadJsonAsync(enabledResponse, HttpStatusCode.OK);
        Assert.False(enabledJson.RootElement.GetProperty("enabled").GetBoolean());
        using var enabledRead = await _client.GetAsync("/v1/admin/media-cleanup-jobs/enabled");
        using var enabledReadJson = await ReadJsonAsync(enabledRead, HttpStatusCode.OK);
        Assert.False(enabledReadJson.RootElement.GetProperty("enabled").GetBoolean());

        using var retentionBody = JsonBody("{\"retentionDays\":14}");
        using var retentionResponse = await _client.PostAsync("/v1/admin/media-cleanup-jobs/simple-retention", retentionBody);
        using var retentionJson = await ReadJsonAsync(retentionResponse, HttpStatusCode.OK);
        Assert.Equal(14, retentionJson.RootElement.GetProperty("retentionDays").GetInt32());
        Assert.True(retentionJson.RootElement.GetProperty("isOverrideActive").GetBoolean());
        using var retentionRead = await _client.GetAsync("/v1/admin/media-cleanup-jobs/simple-retention");
        using var retentionReadJson = await ReadJsonAsync(retentionRead, HttpStatusCode.OK);
        Assert.Equal(14, retentionReadJson.RootElement.GetProperty("retentionDays").GetInt32());

        using var clearBody = JsonBody("{\"retentionDays\":null}");
        using var clearResponse = await _client.PostAsync("/v1/admin/media-cleanup-jobs/simple-retention", clearBody);
        using var clearJson = await ReadJsonAsync(clearResponse, HttpStatusCode.OK);
        Assert.False(clearJson.RootElement.GetProperty("isOverrideActive").GetBoolean());
        _factory.Status.Verify(service => service.SetEnabledAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        _factory.Status.Verify(service => service.SetSimpleRetentionOverrideAsync(14, It.IsAny<CancellationToken>()), Times.Once);
        _factory.Status.Verify(service => service.SetSimpleRetentionOverrideAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("approve", MediaCleanupApprovalStatuses.Approved)]
    [InlineData("reject", MediaCleanupApprovalStatuses.Rejected)]
    public async Task ApprovalActions_BindGuidAndRecordAuthenticatedActor(string action, string status)
    {
        using var response = await _client.PostAsync(
            $"/v1/admin/media-cleanup-jobs/approvals/{MediaRequestDelegateHostFactory.ApprovalId}/{action}", null);
        using var json = await ReadJsonAsync(response, HttpStatusCode.OK);
        var approval = json.RootElement.GetProperty("approval");
        Assert.Equal(MediaRequestDelegateHostFactory.ApprovalId, approval.GetProperty("id").GetGuid());
        Assert.Equal(status, approval.GetProperty("status").GetString());
        Assert.Equal("AdminUser", _factory.DecidedBy);
        _factory.StorageGuard.Verify(service => service.ValidateAsync(It.IsAny<CancellationToken>()),
            action == "approve" ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("DELETE", "/v1/admin/media-assets/415261d0-6e95-40b9-b5f7-f876dc7be9df", null)]
    [InlineData("POST", "/v1/admin/media-assets/restore/415261d0-6e95-40b9-b5f7-f876dc7be9df", null)]
    [InlineData("POST", "/v1/admin/media-assets/cleanup/expired", null)]
    [InlineData("POST", "/v1/admin/media-assets/cleanup/orphaned", null)]
    [InlineData("POST", "/v1/admin/media-assets/cleanup/prune", "{\"daysToKeep\":7}")]
    [InlineData("POST", "/v1/admin/media-cleanup-jobs/enabled", "{\"enabled\":false}")]
    [InlineData("POST", "/v1/admin/media-cleanup-jobs/simple-retention", "{\"retentionDays\":14}")]
    [InlineData("POST", "/v1/admin/media-cleanup-jobs/approvals/54b7c187-3b68-48b0-9b08-75f5814e0b6c/approve", null)]
    [InlineData("POST", "/v1/admin/media-cleanup-jobs/approvals/54b7c187-3b68-48b0-9b08-75f5814e0b6c/reject", null)]
    public async Task EveryAffectedHandler_RequiresMasterKey(string method, string route, string body)
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (body != null) request.Content = JsonBody(body);
        using var response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Media.Invocations);
        Assert.Empty(_factory.Deletion.Invocations);
        Assert.Empty(_factory.Approvals.Invocations);
    }

    [Fact]
    public async Task RealHostOpenApi_MediaContractsMatchCommittedDocument()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(default);
        await using var stream = new MemoryStream();
        await document.SerializeAsJsonAsync(stream, OpenApiSpecVersion.OpenApi3_1, default);
        stream.Position = 0;
        var actual = await JsonNode.ParseAsync(stream);
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            MediaRequestDelegateHostFactory.RepositoryRoot, "Services", "ConduitLLM.Admin", "openapi-admin.json")));
        var expectedPaths = expected["paths"].AsObject().Where(entry =>
            entry.Key.StartsWith("/v1/admin/media-assets", StringComparison.Ordinal) ||
            entry.Key.StartsWith("/v1/admin/media-cleanup-jobs", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(expectedPaths);
        foreach (var (path, contract) in expectedPaths)
        {
            Assert.True(JsonNode.DeepEquals(contract, actual["paths"][path]), $"OpenAPI changed for {path}");
        }
        var pendingSchemas = new Queue<string>(expectedPaths.SelectMany(path => ReferencedSchemas(path.Value)));
        var comparedSchemas = new HashSet<string>();
        while (pendingSchemas.TryDequeue(out var name))
        {
            if (!comparedSchemas.Add(name)) continue;
            var contract = expected["components"]["schemas"][name];
            Assert.True(JsonNode.DeepEquals(contract, actual["components"]["schemas"][name]),
                $"OpenAPI changed for media schema {name}");
            foreach (var dependency in ReferencedSchemas(contract)) pendingSchemas.Enqueue(dependency);
        }
    }

    private static IEnumerable<string> ReferencedSchemas(JsonNode node)
    {
        const string prefix = "#/components/schemas/";
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var value) &&
                value.StartsWith(prefix, StringComparison.Ordinal))
                yield return value[prefix.Length..];
            foreach (var child in obj.SelectMany(entry => ReferencedSchemas(entry.Value))) yield return child;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.SelectMany(ReferencedSchemas)) yield return child;
        }
    }

    private static StringContent JsonBody(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedContentType = "application/json")
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatus, $"Expected {expectedStatus}, got {response.StatusCode}: {body}");
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(body);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}

[CollectionDefinition("MediaRequestDelegateHostEnvironment", DisableParallelization = true)]
public sealed class MediaRequestDelegateHostEnvironmentCollection;

internal sealed class MediaRequestDelegateHostFactory : WebApplicationFactory<ConduitLLM.Admin.Program>
{
    internal const string MasterKey = "media-request-delegate-test-master-key";
    internal static readonly Guid MediaId = Guid.Parse("415261d0-6e95-40b9-b5f7-f876dc7be9df");
    internal static readonly Guid ApprovalId = Guid.Parse("54b7c187-3b68-48b0-9b08-75f5814e0b6c");
    internal static readonly string RepositoryRoot = FindRepositoryRoot();
    private readonly SqliteTestDatabase _database = new();
    private readonly Dictionary<string, string> _savedEnvironment = new();
    private bool _enabled = true;
    private int? _retentionDays = 30;
    private bool _isDisposed;
    internal string DecidedBy { get; private set; }
    internal Mock<IAdminMediaService> Media { get; } = new();
    internal Mock<IMediaDeletionEngine> Deletion { get; } = new();
    internal Mock<IMediaReconciliationService> Reconciliation { get; } = new();
    internal Mock<IMediaCleanupStatusService> Status { get; } = new();
    internal Mock<IMediaCleanupApprovalService> Approvals { get; } = new();
    internal Mock<IDistributedLockProvider> Locks { get; } = new();
    internal Mock<IMediaStorageConfigurationGuard> StorageGuard { get; } = new();

    public MediaRequestDelegateHostFactory()
    {
        try
        {
            SetEnvironment("DATABASE_URL", "postgresql://conduit:conduit@127.0.0.1:1/conduit_media_host_tests");
            SetEnvironment("REDIS_URL", null);
            SetEnvironment("CONDUIT_REDIS_CONNECTION_STRING", null);
            SetEnvironment("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", MasterKey);
            SetEnvironment("CONDUIT_MIGRATION_MODE", "Skip");
            SetEnvironment("CONDUIT_OPENAPI_GENERATION", null);
            SetEnvironment("CONDUIT_MEDIA_STORAGE_TYPE", "InMemory");
            SetEnvironment("ConduitLLM__Messaging__Wolverine__Transport", "InMemory");

            Media.Setup(service => service.DeleteMediaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AdminMediaDeleteResult(true, new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc)));
            Media.Setup(service => service.RestoreMediaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MediaRestoreOutcome.Restored);
            var result = new MediaDeletionEngineResult(FilesDeleted: 2, BytesFreed: 64, OperationStatus: "Completed");
            Deletion.Setup(service => service.ExecuteOperationAsync(It.IsAny<MediaDeletionOperationContext>(),
                    It.IsAny<Func<Task<MediaDeletionEngineResult>>>(), It.IsAny<CancellationToken>()))
                .Returns((MediaDeletionOperationContext _, Func<Task<MediaDeletionEngineResult>> action, CancellationToken _) => action());
            Deletion.Setup(service => service.DeleteAsync(It.IsAny<MediaDeletionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            Deletion.Setup(service => service.Preview(It.IsAny<IEnumerable<MediaRecord>>()))
                .Returns((IEnumerable<MediaRecord> records) => new MediaDeletionPreview(records.Count(), records.Sum(record => record.SizeBytes ?? 0)));
            Reconciliation.Setup(service => service.ReconcileAsync(
                    It.IsAny<MediaDeletionOperationContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            Status.Setup(service => service.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _enabled);
            Status.Setup(service => service.SetEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback((bool enabled, CancellationToken _) => _enabled = enabled).Returns(Task.CompletedTask);
            Status.Setup(service => service.GetSimpleRetentionOverrideAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _retentionDays);
            Status.Setup(service => service.SetSimpleRetentionOverrideAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Callback((int? days, CancellationToken _) => _retentionDays = days).Returns(Task.CompletedTask);
            Status.Setup(service => service.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MediaCleanupStatusDto());
            Approvals.Setup(service => service.ApproveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, string actor, CancellationToken _) => CreateApproval(id, actor, MediaCleanupApprovalStatuses.Approved));
            Approvals.Setup(service => service.RejectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, string actor, CancellationToken _) => CreateApproval(id, actor, MediaCleanupApprovalStatuses.Rejected));
            Approvals.Setup(service => service.ListPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<MediaCleanupApprovalDto>());
            Locks.Setup(service => service.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Mock.Of<IDistributedLockOwnership>());
            // The real scheduler is invoked on approval; its storage guard safely stops
            // further work. The service's scheduler implementation has separate coverage.
            StorageGuard.Setup(service => service.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            _database.Seed(context =>
            {
                context.VirtualKeyGroups.Add(new VirtualKeyGroup
                {
                    Id = 1, GroupName = "media host", Balance = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                context.VirtualKeys.Add(new VirtualKey
                {
                    Id = 1, VirtualKeyGroupId = 1, KeyHash = "media-host-key", KeyName = "media host", IsEnabled = true
                });
                context.MediaRecords.Add(new MediaRecord
                {
                    Id = MediaId, VirtualKeyId = 1, StorageKey = "media-host/expired", MediaType = "image",
                    ContentType = "image/png", SizeBytes = 32, CreatedAt = DateTime.UtcNow.AddDays(-30),
                    ExpiresAt = DateTime.UtcNow.AddDays(-1)
                });
                context.SaveChanges();
            });
        }
        catch
        {
            RestoreEnvironment();
            throw;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Join(RepositoryRoot, "Services", "ConduitLLM.Admin"));
        builder.UseEnvironment("Testing");
        // Default Windows host logging includes EventLog, which requires machine
        // permissions. Console logging preserves middleware diagnostics in CI.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["AdminApi:MasterKey"] = MasterKey,
            ["ConduitLLM:Messaging:Wolverine:Transport"] = "InMemory",
            ["ConduitLLM:Storage:Provider"] = "InMemory"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<DbContextOptions<ConduitDbContext>>();
            services.AddSingleton(_database.Options);
            services.RemoveAll<ConduitDbContext>();
            services.AddScoped(_ => _database.CreateContext());
            services.RemoveAll<IDbContextFactory<ConduitDbContext>>();
            services.AddSingleton(_database.CreateDbContextFactory());
            services.RemoveAll<IConfigurationDbContext>();
            services.AddScoped<IConfigurationDbContext>(provider => provider.GetRequiredService<ConduitDbContext>());
            Replace(services, Media.Object);
            Replace(services, Deletion.Object);
            Replace(services, Reconciliation.Object);
            Replace(services, Status.Object);
            Replace(services, Approvals.Object);
            Replace(services, Locks.Object);
            Replace(services, StorageGuard.Object);
            services.PostConfigure<AdminSecurityOptions>(options =>
            {
                options.RateLimiting.Enabled = false;
                options.IpFiltering.Enabled = false;
            });
        });
    }

    private static void Replace<T>(IServiceCollection services, T service) where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(service);
    }

    private MediaCleanupApproval CreateApproval(Guid id, string actor, string status)
    {
        DecidedBy = actor;
        return new MediaCleanupApproval { Id = id, Status = status, CleanupType = MediaCleanupTypes.Expiration };
    }

    private void SetEnvironment(string name, string value)
    {
        _savedEnvironment.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing)
            {
                RestoreEnvironment();
            }
        }
    }

    private void RestoreEnvironment()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _database.Dispose();
        foreach (var (name, value) in _savedEnvironment)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Join(directory.FullName, "Conduit.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not find Conduit.slnx for the real Admin test host.");
    }
}
