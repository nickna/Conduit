using System.Data.Common;
using System.Text.Json;

using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Filters;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Admin.Services;

public sealed class VirtualKeyGroupMembershipEtagTests
{
    private static readonly DateTime OriginalTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndDeleteChangeTheGroupGetTagWithoutChangingGroupRowMetadata(bool deleteByHash)
    {
        await using var fixture = new Fixture();
        var before = await fixture.GetAsync(1);
        var unrelated = await fixture.GetAsync(2);
        Assert.Equal(0, before.Dto.VirtualKeyCount);

        var key = await fixture.CreateKeyAsync(1);
        var created = await fixture.GetAsync(1);
        Assert.Equal(1, created.Dto.VirtualKeyCount);
        Assert.NotEqual(before.Tag, created.Tag);
        Assert.Equal(before.Dto.UpdatedAt, created.Dto.UpdatedAt);
        Assert.Equal(unrelated.Tag, (await fixture.GetAsync(2)).Tag);

        Assert.True(deleteByHash ? await fixture.Keys.DeleteAsync(key.KeyHash) : await fixture.Keys.DeleteAsync(key.Id));
        var deleted = await fixture.GetAsync(1);
        Assert.Equal(0, deleted.Dto.VirtualKeyCount);
        Assert.NotEqual(created.Tag, deleted.Tag);
        Assert.Equal(before.Dto.UpdatedAt, deleted.Dto.UpdatedAt);
        Assert.Equal(unrelated.Tag, (await fixture.GetAsync(2)).Tag);
        await fixture.AssertFinancialFieldsUnchangedAsync();
    }

    [Fact]
    public async Task MovingAKeyWithAnOldGroupNavigationChangesBothGroupTagsWithoutUpdatingGroupScalars()
    {
        await using var fixture = new Fixture();
        var created = await fixture.CreateKeyAsync(1);
        var sourceBefore = await fixture.GetAsync(1);
        var destinationBefore = await fixture.GetAsync(2);
        var key = (await fixture.Keys.GetByIdAsync(created.Id))!;
        Assert.Equal(1, key.VirtualKeyGroup.Id);
        // A detached navigation must not replace the original group's financial state.
        key.VirtualKeyGroup.Balance = 999m;
        key.VirtualKeyGroupId = 2;
        Assert.True(await fixture.Keys.UpdateAsync(key));

        var sourceAfter = await fixture.GetAsync(1);
        var destinationAfter = await fixture.GetAsync(2);
        Assert.Equal(0, sourceAfter.Dto.VirtualKeyCount);
        Assert.Equal(1, destinationAfter.Dto.VirtualKeyCount);
        Assert.NotEqual(sourceBefore.Tag, sourceAfter.Tag);
        Assert.NotEqual(destinationBefore.Tag, destinationAfter.Tag);
        Assert.Equal(sourceBefore.Dto.UpdatedAt, sourceAfter.Dto.UpdatedAt);
        Assert.Equal(destinationBefore.Dto.UpdatedAt, destinationAfter.Dto.UpdatedAt);
        Assert.Equal(2, (await fixture.Keys.GetByIdAsync(key.Id))!.VirtualKeyGroupId);
        await fixture.AssertFinancialFieldsUnchangedAsync();
    }

    [Fact]
    public async Task ScalarKeyChangesKeepTheCountAndGroupTagStable()
    {
        await using var fixture = new Fixture();
        var created = await fixture.CreateKeyAsync(1);
        var before = await fixture.GetAsync(1);
        var key = (await fixture.Keys.GetByIdAsync(created.Id))!;
        key.KeyName = "renamed";
        key.IsEnabled = false;
        key.ExpiresAt = OriginalTime;
        Assert.True(await fixture.Keys.UpdateAsync(key));

        var after = await fixture.GetAsync(1);
        Assert.Equal(1, after.Dto.VirtualKeyCount);
        Assert.Equal(before.Tag, after.Tag);
        Assert.Equal(before.Dto.UpdatedAt, after.Dto.UpdatedAt);
    }

    [Fact]
    public async Task FreshGetTagGuardsGroupPatchAndReturnedCountTagCanGuardTheNextPatch()
    {
        await using var fixture = new Fixture();
        var before = await fixture.GetAsync(1);
        await fixture.CreateKeyAsync(1);

        var stale = await fixture.PatchAsync(1, before.Tag, "stale patch");
        Assert.Equal(StatusCodes.Status412PreconditionFailed,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(stale.Result).StatusCode);
        Assert.Empty(stale.Tag);
        Assert.Equal("group-one", (await fixture.GetAsync(1)).Dto.GroupName);

        var current = await fixture.GetAsync(1);
        var patched = await fixture.PatchAsync(1, current.Tag, "fresh patch");
        Assert.Equal(StatusCodes.Status200OK,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(patched.Result).StatusCode);
        var dto = Assert.IsType<VirtualKeyGroupDto>(Assert.IsAssignableFrom<IValueHttpResult>(patched.Result).Value);
        Assert.Equal("fresh patch", dto.GroupName);
        Assert.Equal(1, dto.VirtualKeyCount);
        Assert.Equal(dto.ResourceETag, patched.Tag);
        Assert.Equal(patched.Tag, (await fixture.GetAsync(1)).Tag);

        var next = await fixture.PatchAsync(1, patched.Tag, "next patch");
        Assert.Equal(StatusCodes.Status200OK,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(next.Result).StatusCode);
        var nextDto = Assert.IsType<VirtualKeyGroupDto>(Assert.IsAssignableFrom<IValueHttpResult>(next.Result).Value);
        Assert.Equal(1, nextDto.VirtualKeyCount);
        Assert.Equal(nextDto.ResourceETag, next.Tag);
        Assert.Equal(next.Tag, (await fixture.GetAsync(1)).Tag);
        await fixture.AssertFinancialFieldsUnchangedAsync();
    }

    [Fact]
    public async Task GetTagStaysPairedWithItsReturnedCountWhenMembershipChangesAfterTheHandlerRead()
    {
        await using var fixture = new Fixture();
        var before = await fixture.GetAsync(1);
        var snapshot = await fixture.GetAsync(1, async () => { await fixture.CreateKeyAsync(1); });

        Assert.Equal(0, snapshot.Dto.VirtualKeyCount);
        Assert.Equal(before.Tag, snapshot.Tag);
        var latest = await fixture.GetAsync(1);
        Assert.Equal(1, latest.Dto.VirtualKeyCount);
        Assert.NotEqual(snapshot.Tag, latest.Tag);
        Assert.Equal(snapshot.Dto.UpdatedAt, latest.Dto.UpdatedAt);
    }

    [Theory]
    [InlineData("strong")]
    [InlineData("wildcard")]
    [InlineData("alternatives")]
    public async Task MembershipCommitBetweenVersionReadAndCasRejectsStaleOrRetriesMatchingConditionOnce(string conditionKind)
    {
        await using var fixture = new Fixture();
        var before = await fixture.GetAsync(1);
        var row = (await fixture.Groups.GetByIdAsync(1))!;
        var winnerTag = new ResourceVersion(row.RowVersion, row.UpdatedAt) { VirtualKeyCount = 1 }.ETag;
        var candidate = conditionKind switch
        {
            "strong" => before.Tag,
            "wildcard" => "*",
            _ => before.Tag + ", " + winnerTag
        };
        await using var request = fixture.Database.CreateContext();
        var calls = 0;
        fixture.Race.Arm(request, async () => { await fixture.CreateKeyAsync(1); });

        Task<VirtualKeyGroupDto?> MutateAsync() => ResourceMutationGuard.ExecuteAsync<VirtualKeyGroup, VirtualKeyGroupDto>(
            request, 1, new ResourcePrecondition([candidate]), async (db, group, token) =>
            {
                calls++;
                Assert.Equal(1, await db.VirtualKeys.CountAsync(key => key.VirtualKeyGroupId == group.Id, token));
                group.GroupName = "guarded patch";
                group.UpdatedAt = OriginalTime.AddMinutes(1);
                await db.Entry(group).Collection(value => value.VirtualKeys).LoadAsync(token);
            }, VirtualKeyGroupDto.FromEntity);

        if (conditionKind == "strong")
        {
            await Assert.ThrowsAsync<ResourcePreconditionFailedException>(MutateAsync);
            Assert.Equal(0, calls);
            var persisted = await fixture.GetAsync(1);
            Assert.Equal("group-one", persisted.Dto.GroupName);
            Assert.Equal(winnerTag, persisted.Tag);
            Assert.Equal(1, persisted.Dto.VirtualKeyCount);
            Assert.Equal(1, fixture.Race.RequestTransactions);
        }
        else
        {
            var committed = await MutateAsync();
            Assert.NotNull(committed);
            Assert.Equal(1, calls);
            Assert.Equal(2, fixture.Race.RequestTransactions);
            Assert.Equal("guarded patch", committed.GroupName);
            Assert.Equal(1, committed.VirtualKeyCount);
            Assert.Equal(committed.ResourceETag, (await fixture.GetAsync(1)).Tag);
        }
        Assert.Equal(1, fixture.Race.CompetingCommits);
        await fixture.AssertFinancialFieldsUnchangedAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ConduitDbContext _scoped;
        private readonly ServiceProvider _services;
        private readonly HttpContextAccessor _accessor = new();
        private readonly VirtualKeyGroupsEndpoints _endpoints;
        public BeforeTransaction Race { get; } = new();
        public SqliteTestDatabase Database { get; }
        public VirtualKeyRepository Keys { get; }
        public VirtualKeyGroupRepository Groups { get; }

        public Fixture()
        {
            Database = new SqliteTestDatabase(Race);
            var factory = Database.CreateDbContextFactory();
            _scoped = Database.CreateContext();
            _services = new ServiceCollection().AddSingleton(factory).BuildServiceProvider();
            Keys = new VirtualKeyRepository(factory, NullLogger<VirtualKeyRepository>.Instance);
            Groups = new VirtualKeyGroupRepository(factory, NullLogger<VirtualKeyGroupRepository>.Instance);
            _endpoints = new VirtualKeyGroupsEndpoints(Groups, Keys, _scoped, Mock.Of<IRefundService>(),
                _accessor, NullLogger<VirtualKeyGroupsEndpoints>.Instance);
            Database.Seed(db =>
            {
                db.VirtualKeyGroups.AddRange(
                    new VirtualKeyGroup { Id = 1, GroupName = "group-one", Balance = 100m,
                        LifetimeCreditsAdded = 100m, LifetimeSpent = 20m, RateLimitRpm = 30, UpdatedAt = OriginalTime },
                    new VirtualKeyGroup { Id = 2, GroupName = "group-two", Balance = 200m,
                        LifetimeCreditsAdded = 200m, LifetimeSpent = 40m, RateLimitRpm = 60, UpdatedAt = OriginalTime });
                db.SaveChanges();
            });
        }

        public async Task<VirtualKey> CreateKeyAsync(int groupId)
        {
            var key = new VirtualKey { KeyName = "member", KeyHash = Guid.NewGuid().ToString("N"),
                VirtualKeyGroupId = groupId, IsEnabled = true };
            await Keys.CreateAsync(key);
            return key;
        }

        public async Task<(VirtualKeyGroupDto Dto, string Tag)> GetAsync(int id, Func<Task>? afterRead = null)
        {
            var http = Context("GET", id);
            var result = await new VersionedResourceEndpointFilter().InvokeAsync(new Invocation(http), async _ =>
            {
                var snapshot = await _endpoints.GetGroup(id);
                if (afterRead is not null) await afterRead();
                return snapshot;
            });
            var dto = Assert.IsType<VirtualKeyGroupDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            var tag = http.Response.Headers.ETag.ToString();
            Assert.Equal(dto.ResourceETag, tag);
            Assert.NotEmpty(tag);
            return (dto, tag);
        }

        public async Task<(object? Result, string Tag)> PatchAsync(int id, string tag, string name)
        {
            var http = Context("PATCH", id);
            http.Request.Headers.IfMatch = tag;
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { groupName = name }));
            var patch = JsonMergePatchState.Parse<UpdateVirtualKeyGroupRequestDto>(document.RootElement, AdminJsonOptions.Create());
            var result = await new VersionedResourceEndpointFilter().InvokeAsync(new Invocation(http), async _ =>
                await _endpoints.UpdateGroup(id, patch, VersionedResourceEndpointFilter.ReadPrecondition(http)));
            return (result, http.Response.Headers.ETag.ToString());
        }

        private DefaultHttpContext Context(string method, int id)
        {
            var http = new DefaultHttpContext { RequestServices = _services };
            http.Request.Method = method;
            http.Request.Path = $"/v1/admin/virtual-key-groups/{id}";
            http.Request.RouteValues["id"] = id;
            _accessor.HttpContext = http;
            return http;
        }

        public async Task AssertFinancialFieldsUnchangedAsync()
        {
            await using var db = Database.CreateContext();
            var rows = await db.VirtualKeyGroups.AsNoTracking().OrderBy(group => group.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(100m, rows[0].Balance);
            Assert.Equal(100m, rows[0].LifetimeCreditsAdded);
            Assert.Equal(20m, rows[0].LifetimeSpent);
            Assert.Equal(30, rows[0].RateLimitRpm);
            Assert.Equal(200m, rows[1].Balance);
            Assert.Equal(200m, rows[1].LifetimeCreditsAdded);
            Assert.Equal(40m, rows[1].LifetimeSpent);
            Assert.Equal(60, rows[1].RateLimitRpm);
            Assert.Empty(await db.VirtualKeyGroupTransactions.ToListAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await _scoped.DisposeAsync();
            await _services.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class Invocation(HttpContext http) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = http;
        public override IList<object?> Arguments { get; } = [];
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    private sealed class BeforeTransaction : DbTransactionInterceptor
    {
        private ConduitDbContext? _request;
        private Func<Task>? _winner;
        public int RequestTransactions { get; private set; }
        public int CompetingCommits { get; private set; }
        public void Arm(ConduitDbContext request, Func<Task> winner)
        {
            _request = request;
            _winner = winner;
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (!ReferenceEquals(eventData.Context, _request)) return result;
            RequestTransactions++;
            var winner = Interlocked.Exchange(ref _winner, null);
            if (winner is not null)
            {
                await winner();
                CompetingCommits++;
            }
            return result;
        }
    }
}
