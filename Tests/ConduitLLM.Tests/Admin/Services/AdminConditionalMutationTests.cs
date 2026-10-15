using System.Data.Common;
using System.Text.Json;
using System.Text;
using ConduitLLM.Tests.Admin.Endpoints;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Filters;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs.IpFilter;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Tests.TestInfrastructure;
using ConduitLLM.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Admin.Services;

public sealed class AdminConditionalMutationTests
{
    [Theory]
    [InlineData("group-adjust", false)]
    [InlineData("group-adjust", true)]
    [InlineData("group-refund", false)]
    [InlineData("group-refund", true)]
    public async Task FinancialGroupRoutes_UseMembershipTagAndRejectCountOnlyRace(string operation, bool race)
    {
        await using var fixture = await Fixture.CreateAsync(operation);
        var keys = new VirtualKeyRepository(fixture.Database.CreateDbContextFactory(),
            NullLogger<VirtualKeyRepository>.Instance);
        var memberId = await keys.CreateAsync(new VirtualKey
        {
            KeyName = "member", KeyHash = "member-hash", VirtualKeyGroupId = 1
        });
        await keys.CreateAsync(new VirtualKey
        {
            KeyName = "second-member", KeyHash = "second-member-hash", VirtualKeyGroupId = 1
        });
        using var host = fixture.CreateHost();
        using var get = await host.Client.GetAsync("/v1/admin/virtual-key-groups/1");
        Assert.Equal(200, (int)get.StatusCode);
        using (var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync()))
            Assert.Equal(2, body.RootElement.GetProperty("virtualKeyCount").GetInt32());
        var tag = get.Headers.ETag!.Tag;
        using var keyGroupGet = await host.Client.GetAsync($"/v1/admin/virtual-keys/{memberId}/group");
        Assert.Equal(200, (int)keyGroupGet.StatusCode);
        Assert.Equal(tag, keyGroupGet.Headers.ETag!.Tag);
        using (var body = JsonDocument.Parse(await keyGroupGet.Content.ReadAsStringAsync()))
            Assert.Equal(2, body.RootElement.GetProperty("virtualKeyCount").GetInt32());
        if (race)
            fixture.Race.Arm(() => keys.CreateAsync(new VirtualKey
            {
                KeyName = "concurrent-member", KeyHash = "concurrent-hash", VirtualKeyGroupId = 1
            }));

        using var request = fixture.CreateHttpRequest(tag);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(race ? 412 : 200, (int)response.StatusCode);
        if (race)
        {
            await using var db = fixture.Database.CreateContext();
            Assert.Equal(100m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
            Assert.Equal(3, await db.VirtualKeys.CountAsync());
            Assert.Empty(await db.RefundIdempotencyRecords.ToListAsync());
            fixture.Cost.Verify(cost => cost.CalculateRefundAsync(It.IsAny<string>(), It.IsAny<Usage>(),
                It.IsAny<Usage>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<ProviderCostRefundContext?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            using var freshGet = await host.Client.GetAsync("/v1/admin/virtual-key-groups/1");
            Assert.Equal(freshGet.Headers.ETag!.Tag, response.Headers.ETag!.Tag);
            using var repeated = fixture.CreateHttpRequest(response.Headers.ETag.Tag);
            using var repeatedResponse = await host.Client.SendAsync(repeated);
            Assert.Equal(200, (int)repeatedResponse.StatusCode);
        }
    }

    public static IEnumerable<object[]> HttpMutations()
    {
        foreach (var operation in new[] { "key-patch", "key-delete", "group-patch", "group-delete",
                     "group-adjust", "group-refund", "ip-patch", "ip-delete" })
        foreach (var race in new[] { false, true }) yield return [operation, race];
    }

    [Theory]
    [MemberData(nameof(HttpMutations))]
    public async Task MappedHttpRoutes_RequireAndAtomicallyEnforceIfMatch(string operation, bool race)
    {
        await using var fixture = await Fixture.CreateAsync(operation);
        using var host = fixture.CreateHost();
        using (var missingHeader = fixture.CreateHttpRequest(null))
        using (var missingResponse = await host.Client.SendAsync(missingHeader))
            Assert.Equal(428, (int)missingResponse.StatusCode);
        var tag = await fixture.ReadTagAsync();
        if (race) fixture.Race.Arm(fixture.CommitWinnerAsync);
        using var request = fixture.CreateHttpRequest(tag);
        using var response = await host.Client.SendAsync(request);
        var expected = race ? 412 : operation.EndsWith("delete", StringComparison.Ordinal) ? 204 : 200;
        Assert.Equal(expected, (int)response.StatusCode);
        if (race)
        {
            Assert.Equal("winner", await fixture.ReadNameAsync());
            fixture.Storage.Verify(storage => storage.DeleteManyAsync(It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        else if (expected == 200)
        {
            Assert.Equal(await fixture.ReadTagAsync(), response.Headers.ETag!.Tag);
            Assert.DoesNotContain("resourceETag", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("key-patch")]
    [InlineData("key-delete")]
    [InlineData("group-patch")]
    [InlineData("group-delete")]
    [InlineData("group-adjust")]
    [InlineData("group-refund")]
    [InlineData("ip-patch")]
    [InlineData("ip-delete")]
    public async Task EveryGuardedMutation_RejectsCommitBetweenPreflightAndStoreLock(string operation)
    {
        await using var fixture = await Fixture.CreateAsync(operation);
        var tag = await fixture.ReadTagAsync();
        fixture.Race.Arm(fixture.CommitWinnerAsync);
        var result = await fixture.InvokeAsync(tag);

        Assert.Equal(412, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(1, fixture.Race.CompetingCommits);
        Assert.Equal("winner", await fixture.ReadNameAsync());
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(operation == "group-refund" ? 1 : 0, await db.VirtualKeyGroupTransactions.CountAsync());
        Assert.Empty(await db.RefundIdempotencyRecords.ToListAsync());
        fixture.Storage.Verify(storage => storage.DeleteManyAsync(It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Cost.Verify(cost => cost.CalculateRefundAsync(It.IsAny<string>(), It.IsAny<Usage>(),
            It.IsAny<Usage>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<ProviderCostRefundContext?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("key-patch")]
    [InlineData("key-delete")]
    [InlineData("group-patch")]
    [InlineData("group-delete")]
    [InlineData("group-adjust")]
    [InlineData("group-refund")]
    [InlineData("ip-patch")]
    [InlineData("ip-delete")]
    public async Task EveryGuardedMutation_SucceedsInOneTransactionWithUsableSnapshotTag(string operation)
    {
        await using var fixture = await Fixture.CreateAsync(operation);
        var result = await fixture.InvokeAsync(await fixture.ReadTagAsync());
        var expectedStatus = operation.EndsWith("delete", StringComparison.Ordinal) ? 204 : 200;
        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        if (expectedStatus == 200)
        {
            Assert.Equal(await fixture.ReadTagAsync(), fixture.Http.Response.Headers.ETag);
            var dto = Assert.IsAssignableFrom<IVersionedRepresentation>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            Assert.Equal(dto.ResourceETag, fixture.Http.Response.Headers.ETag);
        }
        await using var db = fixture.Database.CreateContext();
        if (operation == "key-delete")
        {
            // Production engine + repository + independent SQLite connections. The engine's
            // record purge must use the guarded connection, or this blocks after storage deletion.
            Assert.Empty(await db.VirtualKeys.ToListAsync());
            Assert.Empty(await db.MediaRecords.IgnoreQueryFilters().ToListAsync());
            fixture.Storage.Verify(storage => storage.DeleteManyAsync(It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        if (operation == "group-adjust")
        {
            Assert.Equal(105m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
            var ledger = await db.VirtualKeyGroupTransactions.SingleAsync();
            Assert.Equal(5m, ledger.Amount);
            Assert.Equal(105m, ledger.BalanceAfter);
        }
        if (operation == "group-refund")
        {
            Assert.Equal(103m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
            Assert.Equal(2, await db.VirtualKeyGroupTransactions.CountAsync());
            Assert.Single(await db.RefundIdempotencyRecords.ToListAsync());
        }
    }

    [Fact]
    public async Task WildcardKeyPatch_MergesMetadataAndLimitsFromLockedWinnerAndValidatesAliases()
    {
        await using var fixture = await Fixture.CreateAsync("key-patch");
        fixture.KeyPatch = """{"metadata":{"added":"request"},"modelRateLimits":{"known":{"rpm":7}}}""";
        await fixture.Database.SeedAsync(async db =>
        {
            var provider = new Provider { ProviderName = "test", ProviderType = ProviderType.OpenAI };
            var model = ModelTestHelper.CreateCompleteTestModel("known");
            db.ModelProviderMappings.Add(new ModelProviderMapping
            {
                ModelAlias = "known", ProviderModelId = "known", Provider = provider,
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                { Model = model, Identifier = "known", Provider = ProviderType.OpenAI }
            });
            await db.SaveChangesAsync();
        });
        fixture.Race.Arm(async () =>
        {
            await fixture.CommitWinnerAsync();
            await using var db = fixture.Database.CreateContext();
            var key = await db.VirtualKeys.SingleAsync();
            key.Metadata = """{"retained":"winner"}""";
            key.ModelRateLimits = """{"known":{"rpm":2,"tpm":900}}""";
            await db.SaveChangesAsync();
        });

        var response = Assert.IsType<VirtualKeyDto>(Assert.IsAssignableFrom<IValueHttpResult>(
            await fixture.InvokeAsync("*")).Value);
        Assert.Equal("winner", response.Metadata!["retained"].GetString());
        Assert.Equal("request", response.Metadata["added"].GetString());
        Assert.Equal(7, response.ModelRateLimits!["known"].Rpm);
        Assert.Equal(900, response.ModelRateLimits["known"].Tpm);

        fixture.KeyPatch = """{"modelRateLimits":{"unknown":{"rpm":1}}}""";
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.InvokeAsync("*"));
        await using var persisted = fixture.Database.CreateContext();
        Assert.DoesNotContain("unknown", (await persisted.VirtualKeys.SingleAsync()).ModelRateLimits!);
    }

    [Fact]
    public async Task FailedMediaCleanup_RollsBackDatabaseAndKeepsKey()
    {
        await using var fixture = await Fixture.CreateAsync("key-delete");
        fixture.Storage.Setup(storage => storage.DeleteManyAsync(It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) => new MediaBulkDeleteResult
            {
                Items = keys.Select(key => new MediaDeleteItemResult { StorageKey = key, Deleted = false,
                    ErrorCode = "denied" }).ToList()
            });
        var tag = await fixture.ReadTagAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync(tag));
        Assert.Equal(tag, await fixture.ReadTagAsync());
        await using var db = fixture.Database.CreateContext();
        Assert.Single(await db.VirtualKeys.ToListAsync());
        Assert.Single(await db.MediaRecords.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RefundReplay_StillRequiresCurrentGroupPrecondition()
    {
        await using var fixture = await Fixture.CreateAsync("group-refund");
        var originalTag = await fixture.ReadTagAsync();
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await fixture.InvokeAsync(originalTag)).StatusCode);
        Assert.Equal(412, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await fixture.InvokeAsync(originalTag)).StatusCode);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await fixture.InvokeAsync(await fixture.ReadTagAsync())).StatusCode);
        await using var db = fixture.Database.CreateContext();
        Assert.Equal(2, await db.VirtualKeyGroupTransactions.CountAsync());
        Assert.Equal(103m, (await db.VirtualKeyGroups.SingleAsync()).Balance);
    }

    [Fact]
    public async Task ConditionalRefund_ReturnsCommittedSnapshotWhenGroupIsDeletedBeforeResponse()
    {
        await using var fixture = await Fixture.CreateAsync("group-refund");
        var tag = await fixture.ReadTagAsync();
        string? committedTag = null;
        var refund = new Mock<IRefundService>();
        refund.Setup(service => service.ProcessRefundConditionalAsync(It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<Usage>(), It.IsAny<Usage>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<ResourcePrecondition>(), It.IsAny<CancellationToken>()))
            .Returns(async (int id, string model, Usage original, Usage usage, string reason, string originalId,
                string operationId, string initiatedBy, string? userId, int? logId,
                ResourcePrecondition condition, CancellationToken ct) =>
            {
                var result = await fixture.RefundService.ProcessRefundConditionalAsync(id, model, original, usage,
                    reason, originalId, operationId, initiatedBy, userId, logId, condition, ct);
                committedTag = result.ResourceETag;
                // The production service has committed. Remove the group on an independent
                // connection before allowing the mapped endpoint to build its response.
                await using var db = fixture.Database.CreateContext();
                await db.VirtualKeyGroups.Where(group => group.Id == id).ExecuteDeleteAsync(ct);
                return result;
            });
        using var host = fixture.CreateHost(services => services.AddSingleton(refund.Object));
        using var request = fixture.CreateHttpRequest(tag);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.NotNull(committedTag);
        Assert.Equal(committedTag, response.Headers.ETag!.Tag);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(103m, body.RootElement.GetProperty("balanceAfter").GetDecimal());
        Assert.Equal(3m, body.RootElement.GetProperty("refundAmount").GetDecimal());
        Assert.True(body.RootElement.GetProperty("transactionId").GetInt64() > 0);
        await using var persisted = fixture.Database.CreateContext();
        Assert.Empty(await persisted.VirtualKeyGroups.ToListAsync());
    }

    [Fact]
    public async Task ConditionalRefund_OverRefundRollsBackAndAllowsSameOperationToRetry()
    {
        await using var fixture = await Fixture.CreateAsync("group-refund");
        var tag = await fixture.ReadTagAsync();
        var calculated = new RefundResult
        {
            ModelId = "model", RefundAmount = 11m, RefundReason = "test",
            OriginalUsage = new Usage(), RefundUsage = new Usage()
        };
        fixture.Cost.Setup(cost => cost.CalculateRefundAsync(It.IsAny<string>(), It.IsAny<Usage>(),
                It.IsAny<Usage>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<ProviderCostRefundContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(calculated);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync(tag));
        Assert.Contains("Refund would exceed original transaction", rejected.Message);
        Assert.Equal(tag, await fixture.ReadTagAsync());
        await using (var unchanged = fixture.Database.CreateContext())
        {
            Assert.Equal(100m, (await unchanged.VirtualKeyGroups.SingleAsync()).Balance);
            Assert.Equal(TransactionType.Debit, (await unchanged.VirtualKeyGroupTransactions.SingleAsync()).TransactionType);
            Assert.Empty(await unchanged.RefundIdempotencyRecords.ToListAsync());
        }

        // A rejected conditional callback must release its transaction and must not reserve
        // the operation ID. Retrying corrected data through the same scoped service can commit.
        calculated.RefundAmount = 3m;
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await fixture.InvokeAsync(tag)).StatusCode);
        await using var committed = fixture.Database.CreateContext();
        Assert.Equal(103m, (await committed.VirtualKeyGroups.SingleAsync()).Balance);
        Assert.Equal(2, await committed.VirtualKeyGroupTransactions.CountAsync());
        Assert.Single(await committed.RefundIdempotencyRecords.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly DateTime InitialTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        private readonly string _operation;
        private readonly ConduitDbContext _scoped;
        private readonly VirtualKeysEndpoints _keys;
        private readonly VirtualKeyGroupsEndpoints _groups;
        private readonly IpFilterEndpoints _ips;
        private readonly ServiceProvider _services;
        private readonly IAdminVirtualKeyService _keyService;
        private readonly IAdminIpFilterService _ipService;
        private readonly IVirtualKeyGroupRepository _groupRepository;
        private readonly IVirtualKeyRepository _keyRepository;
        private readonly IRefundService _refundService;
        public SqliteTestDatabase Database { get; }
        public BeforeTransaction Race { get; } = new();
        public DefaultHttpContext Http { get; } = new();
        public Mock<IMediaStorageService> Storage { get; } = new();
        public Mock<ICostCalculationService> Cost { get; } = new();
        public string KeyPatch { get; set; } = """{"keyName":"patched"}""";
        public IRefundService RefundService => _refundService;

        private Fixture(string operation)
        {
            _operation = operation;
            Database = new SqliteTestDatabase(Race);
            var factory = Database.CreateDbContextFactory();
            _scoped = Database.CreateContext();
            _services = new ServiceCollection().AddSingleton(factory).BuildServiceProvider();
            Http.RequestServices = _services;
            Http.Request.RouteValues["id"] = 1;
            var accessor = Mock.Of<IHttpContextAccessor>(accessor => accessor.HttpContext == Http);
            var groups = new VirtualKeyGroupRepository(factory, NullLogger<VirtualKeyGroupRepository>.Instance);
            var keys = new VirtualKeyRepository(factory, NullLogger<VirtualKeyRepository>.Instance);
            _groupRepository = groups;
            _keyRepository = keys;
            Storage.Setup(storage => storage.DeleteManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> storageKeys, CancellationToken _) => new MediaBulkDeleteResult
                {
                    Items = storageKeys.Select(key => new MediaDeleteItemResult { StorageKey = key, Deleted = true }).ToList()
                });
            var budget = new Mock<IMediaDeletionBudgetService>();
            budget.Setup(service => service.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int requested, int _, CancellationToken _) => new MediaDeletionBudgetReservation(requested, requested, requested));
            var storageGuard = new Mock<IMediaStorageConfigurationGuard>();
            storageGuard.Setup(guard => guard.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var engine = new MediaDeletionEngine(Storage.Object, budget.Object,
                new MediaRecordRepository(factory, NullLogger<MediaRecordRepository>.Instance),
                Mock.Of<IMediaCleanupStatusService>(), Mock.Of<IMediaCleanupApprovalService>(), storageGuard.Object,
                Options.Create(new MediaLifecycleOptions { DryRunMode = false, EnableSoftDelete = false,
                    RequireManualApprovalForLargeBatches = false }), NullLogger<MediaDeletionEngine>.Instance);
            var locks = new Mock<IDistributedLockProvider>();
            locks.Setup(service => service.TryAcquireAsync(MediaCleanupLock.Key, TimeSpan.Zero, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDistributedLockOwnership>());
            var keyService = new AdminVirtualKeyService(keys, new VirtualKeySpendHistoryRepository(factory,
                NullLogger<VirtualKeySpendHistoryRepository>.Instance), groups, NullLogger<AdminVirtualKeyService>.Instance,
                Mock.Of<IModelProviderMappingRepository>(), Mock.Of<IModelCapabilityService>(), factory,
                mediaLifecycleService: Mock.Of<IMediaLifecycleService>(), mediaDeletionEngine: engine,
                mediaCleanupLockService: locks.Object);
            var ipService = new AdminIpFilterService(new IpFilterRepository(factory, NullLogger<IpFilterRepository>.Instance),
                Mock.Of<IGlobalSettingRepository>(), Mock.Of<IOptionsMonitor<IpFilterOptions>>(),
                NullLogger<AdminIpFilterService>.Instance, dbContextFactory: factory);
            _keyService = keyService;
            _ipService = ipService;
            Cost.Setup(cost => cost.CalculateRefundAsync(It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<Usage>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<ProviderCostRefundContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RefundResult { ModelId = "model", RefundAmount = 3m, RefundReason = "test",
                    OriginalUsage = new Usage(), RefundUsage = new Usage() });
            _keys = new VirtualKeysEndpoints(keyService, accessor, NullLogger<VirtualKeysEndpoints>.Instance);
            _ips = new IpFilterEndpoints(ipService, accessor, NullLogger<IpFilterEndpoints>.Instance);
            _refundService = new RefundService(Cost.Object, groups, _scoped, NullLogger<RefundService>.Instance);
            _groups = new VirtualKeyGroupsEndpoints(groups, keys, _scoped, _refundService,
                accessor, NullLogger<VirtualKeyGroupsEndpoints>.Instance);
        }

        public AdminEndpointTestHost CreateHost(Action<IServiceCollection>? configure = null) => AdminEndpointTestHost.Create(services =>
        {
            services.AddHttpContextAccessor();
            services.AddSingleton(Database.CreateDbContextFactory());
            services.AddSingleton(_keyService);
            services.AddSingleton(_ipService);
            services.AddSingleton(_groupRepository);
            services.AddSingleton(_keyRepository);
            services.AddSingleton(_refundService);
            services.AddSingleton<IConfigurationDbContext>(_scoped);
            services.AddSingleton<VirtualKeysEndpoints>();
            services.AddSingleton<VirtualKeyGroupsEndpoints>();
            services.AddSingleton<IpFilterEndpoints>();
            services.ConfigureHttpJsonOptions(options => AdminJsonOptions.Configure(options.SerializerOptions));
            services.AddSingleton(AdminJsonOptions.Create());
            configure?.Invoke(services);
        }, endpoints =>
        {
            VirtualKeysEndpoints.MapVirtualKeysEndpoints(endpoints);
            VirtualKeyGroupsEndpoints.MapVirtualKeyGroupsEndpoints(endpoints);
            IpFilterEndpoints.MapIpFilterEndpoints(endpoints);
        });

        public HttpRequestMessage CreateHttpRequest(string? tag)
        {
            var path = _operation.StartsWith("key", StringComparison.Ordinal) ? "/v1/admin/virtual-keys/1" :
                _operation.StartsWith("ip", StringComparison.Ordinal) ? "/v1/admin/ip-filters/1" : "/v1/admin/virtual-key-groups/1";
            if (_operation == "group-adjust") path += "/adjust-balance";
            if (_operation == "group-refund") path += "/refund";
            var method = _operation.EndsWith("delete", StringComparison.Ordinal) ? HttpMethod.Delete :
                _operation.EndsWith("patch", StringComparison.Ordinal) ? HttpMethod.Patch : HttpMethod.Post;
            var request = new HttpRequestMessage(method, path);
            if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
            if (method != HttpMethod.Delete)
            {
                var json = _operation switch
                {
                    "key-patch" => KeyPatch,
                    "group-patch" => """{"groupName":"patched"}""",
                    "ip-patch" => """{"name":"patched"}""",
                    "group-adjust" => """{"amount":5}""",
                    "group-refund" => """{"modelId":"model","refundReason":"test","originalTransactionId":"1","originalUsage":{"promptTokens":10,"completionTokens":0,"totalTokens":10},"refundUsage":{"promptTokens":3,"completionTokens":0,"totalTokens":3}}""",
                    _ => throw new InvalidOperationException(_operation)
                };
                request.Content = new StringContent(json, Encoding.UTF8,
                    method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
            }
            if (_operation == "group-refund") request.Headers.Add("Idempotency-Key", "refund-one");
            return request;
        }

        public static async Task<Fixture> CreateAsync(string operation)
        {
            var fixture = new Fixture(operation);
            await fixture.Database.SeedAsync(async db =>
            {
                db.VirtualKeyGroups.Add(new VirtualKeyGroup { Id = 1, GroupName = "before", Balance = 100m,
                    LifetimeCreditsAdded = 100m, UpdatedAt = InitialTime });
                db.IpFilters.Add(new IpFilterEntity { Id = 1, Name = "before", FilterType = "blacklist",
                    IpAddressOrCidr = "192.0.2.1", UpdatedAt = InitialTime });
                if (operation.StartsWith("key", StringComparison.Ordinal))
                    db.VirtualKeys.Add(new VirtualKey { Id = 1, KeyName = "before", KeyHash = "hash",
                        VirtualKeyGroupId = 1, UpdatedAt = InitialTime });
                if (operation == "key-delete")
                    db.MediaRecords.Add(new MediaRecord { Id = Guid.NewGuid(), VirtualKeyId = 1,
                        StorageKey = "media-one", MediaType = "image", SizeBytes = 100, CreatedAt = InitialTime });
                if (operation == "group-refund")
                    db.VirtualKeyGroupTransactions.Add(new VirtualKeyGroupTransaction { Id = 1,
                        VirtualKeyGroupId = 1, TransactionType = TransactionType.Debit, Amount = 10m,
                        BalanceAfter = 100m, ReferenceType = ReferenceType.Manual, InitiatedBy = "test",
                        CreatedAt = InitialTime });
                await db.SaveChangesAsync();
            });
            return fixture;
        }

        public async Task<object?> InvokeAsync(string tag)
        {
            Http.Response.Headers.Clear();
            Http.Request.Headers.IfMatch = tag;
            var condition = VersionedResourceEndpointFilter.ReadPrecondition(Http)!;
            Http.Request.Method = _operation.EndsWith("delete", StringComparison.Ordinal) ? "DELETE" :
                _operation.EndsWith("patch", StringComparison.Ordinal) ? "PATCH" : "POST";
            Http.Request.Path = _operation.StartsWith("key", StringComparison.Ordinal) ? "/v1/admin/virtual-keys/1" :
                _operation.StartsWith("ip", StringComparison.Ordinal) ? "/v1/admin/ip-filters/1" : "/v1/admin/virtual-key-groups/1";
            if (_operation == "group-adjust") Http.Request.Path += "/adjust-balance";
            if (_operation == "group-refund") Http.Request.Path += "/refund";
            return await new VersionedResourceEndpointFilter().InvokeAsync(new Invocation(Http), async _ =>
            {
                return _operation switch
                {
                    "key-patch" => await _keys.UpdateKey(1, Parse<UpdateVirtualKeyRequestDto>(KeyPatch), condition),
                    "key-delete" => await _keys.DeleteKey(1, precondition: condition),
                    "group-patch" => await _groups.UpdateGroup(1, Parse<UpdateVirtualKeyGroupRequestDto>("""{"groupName":"patched"}"""), condition),
                    "group-delete" => await _groups.DeleteGroup(1, condition),
                    "group-adjust" => await _groups.AdjustBalance(1, new AdjustBalanceDto { Amount = 5m }, condition),
                    "group-refund" => await _groups.ProcessRefund(1, new ProcessRefundRequestDto
                    {
                        ModelId = "model", RefundReason = "test", OriginalTransactionId = "1",
                        OriginalUsage = new UsageDto(), RefundUsage = new UsageDto()
                    }, "refund-one", condition),
                    "ip-patch" => await _ips.UpdateFilter(1, Parse<UpdateIpFilterDto>("""{"name":"patched"}"""), condition),
                    "ip-delete" => await _ips.DeleteFilter(1, condition),
                    _ => throw new InvalidOperationException(_operation)
                };
            });
        }

        public async Task<string> ReadTagAsync()
        {
            await using var db = Database.CreateContext();
            var version = _operation.StartsWith("key", StringComparison.Ordinal)
                ? await ResourceMutationGuard.ReadVersionAsync<VirtualKey>(db, 1)
                : _operation.StartsWith("ip", StringComparison.Ordinal)
                    ? await ResourceMutationGuard.ReadVersionAsync<IpFilterEntity>(db, 1)
                    : await ResourceMutationGuard.ReadVersionAsync<VirtualKeyGroup>(db, 1);
            return version!.ETag;
        }

        public async Task<string> ReadNameAsync()
        {
            await using var db = Database.CreateContext();
            if (_operation.StartsWith("key", StringComparison.Ordinal)) return (await db.VirtualKeys.SingleAsync()).KeyName;
            if (_operation.StartsWith("ip", StringComparison.Ordinal)) return (await db.IpFilters.SingleAsync()).Name!;
            return (await db.VirtualKeyGroups.SingleAsync()).GroupName;
        }

        public async Task CommitWinnerAsync()
        {
            await using var db = Database.CreateContext();
            object entity;
            string name;
            if (_operation.StartsWith("key", StringComparison.Ordinal)) { entity = await db.VirtualKeys.SingleAsync(); name = "KeyName"; }
            else if (_operation.StartsWith("ip", StringComparison.Ordinal)) { entity = await db.IpFilters.SingleAsync(); name = "Name"; }
            else { entity = await db.VirtualKeyGroups.SingleAsync(); name = "GroupName"; }
            db.Entry(entity).Property(name).CurrentValue = "winner";
            db.Entry(entity).Property("UpdatedAt").CurrentValue = InitialTime.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _scoped.DisposeAsync();
            await _services.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private static T Parse<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        return JsonMergePatchState.Parse<T>(document.RootElement, AdminJsonOptions.Create());
    }

    private sealed class Invocation(HttpContext context) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = context;
        public override IList<object?> Arguments { get; } = [];
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    private sealed class BeforeTransaction : DbTransactionInterceptor
    {
        private Func<Task>? _winner;
        public int CompetingCommits { get; private set; }
        public void Arm(Func<Task> winner) => _winner = winner;
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var winner = Interlocked.Exchange(ref _winner, null);
            if (winner is not null) { await winner(); CompetingCommits++; }
            return result;
        }
    }
}
