using ConduitLLM.Admin.Filters;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace ConduitLLM.Tests.Admin.Filters;

public sealed class IpFilterVersionEndpointFilterTests
{
    private const int FilterId = 73;
    private static readonly DateTime OriginalTime = new(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc);
    private static readonly DateTime ChangedTime = OriginalTime.AddMinutes(1);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task BothReadersEmitStrongByteOrTimestampTagsForGetAndHead(bool runtime, int versionKind)
    {
        await using var fixture = await Fixture.CreateAsync(runtime, Row(Version(versionKind)));
        var before = await fixture.ReadRowAsync();
        using var request = new CancellationTokenSource();
        var expected = ExpectedTag(Version(versionKind), OriginalTime);

        foreach (var method in new[] { "GET", "HEAD" })
        {
            fixture.ClearReads();
            var context = Context(method, fixture.Services, request.Token);
            var handlerResult = Results.Ok();
            var handlerCalls = 0;

            var result = await fixture.Filter.InvokeAsync(context, _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult<object?>(handlerResult);
            });

            Assert.Same(handlerResult, result);
            Assert.Equal(1, handlerCalls);
            Assert.Equal(expected, context.HttpContext.Response.Headers.ETag.ToString());
            Assert.Equal(Fingerprint(before), Fingerprint(await fixture.ReadRowAsync()));
            fixture.AssertReads(2, request.Token);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingMutationsRejectMissingOrStaleHeadersWithoutChangingTheRow(bool runtime, bool stale)
    {
        await using var fixture = await Fixture.CreateAsync(runtime, Row([1, 2, 3, 4]));
        var before = await fixture.ReadRowAsync();
        using var request = new CancellationTokenSource();

        foreach (var method in new[] { "PATCH", "DELETE" })
        {
            fixture.ClearReads();
            var context = Context(method, fixture.Services, request.Token);
            if (stale) context.HttpContext.Request.Headers.IfMatch = "\"stale\"";
            var handlerCalls = 0;

            var result = await fixture.Filter.InvokeAsync(context, _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

            Assert.Equal(0, handlerCalls);
            Assert.Equal(stale ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status428PreconditionRequired,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.False(context.HttpContext.Response.Headers.ContainsKey("ETag"));
            Assert.Equal(Fingerprint(before), Fingerprint(await fixture.ReadRowAsync()));
            fixture.AssertReads(1, request.Token);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task MatchingWildcardAndCommaHeadersRefreshTheTagAfterTheHandler(bool runtime, int headerKind)
    {
        await using var fixture = await Fixture.CreateAsync(runtime, Row([1, 2, 3, 4]));
        using var request = new CancellationTokenSource();
        var originalTag = ExpectedTag([1, 2, 3, 4], OriginalTime);
        var context = Context("PATCH", fixture.Services, request.Token);
        context.HttpContext.Request.Headers.IfMatch = headerKind switch
        {
            0 => originalTag,
            1 => "*",
            _ => $" \"stale\" , {originalTag} , \"another\" "
        };
        var handlerResult = Results.Ok();
        var handlerCalls = 0;

        var result = await fixture.Filter.InvokeAsync(context, async _ =>
        {
            handlerCalls++;
            var changed = Row([5, 6, 7, 8]);
            changed.UpdatedAt = ChangedTime;
            changed.Description = "updated café 雪";
            await fixture.WriteRowAsync(changed);
            return handlerResult;
        });

        Assert.Same(handlerResult, result);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(ExpectedTag([5, 6, 7, 8], ChangedTime), context.HttpContext.Response.Headers.ETag.ToString());
        Assert.NotEqual(originalTag, context.HttpContext.Response.Headers.ETag.ToString());
        var stored = Assert.IsType<IpFilterEntity>(await fixture.ReadRowAsync());
        Assert.Equal("updated café 雪", stored.Description);
        Assert.Equal(ChangedTime, stored.UpdatedAt);
        Assert.Equal(OriginalTime.AddHours(-1), stored.CreatedAt);
        fixture.AssertReads(2, request.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOpPatchKeepsThePersistedTimestampAndTag(bool runtime)
    {
        await using var fixture = await Fixture.CreateAsync(runtime, Row(null));
        var before = await fixture.ReadRowAsync();
        var expected = ExpectedTag(null, OriginalTime);
        var context = Context("PATCH", fixture.Services);
        context.HttpContext.Request.Headers.IfMatch = expected;

        var result = await fixture.Filter.InvokeAsync(context,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(expected, context.HttpContext.Response.Headers.ETag.ToString());
        Assert.Equal(Fingerprint(before), Fingerprint(await fixture.ReadRowAsync()));
        fixture.AssertReads(2, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task ActualDeletionRetainsTheOriginalTagWhenTheSecondReadFindsNoRow(bool runtime, int versionKind)
    {
        var version = Version(versionKind);
        await using var fixture = await Fixture.CreateAsync(runtime, Row(version));
        var expected = ExpectedTag(version, OriginalTime);
        var context = Context("DELETE", fixture.Services);
        context.HttpContext.Request.Headers.IfMatch = expected;

        var result = await fixture.Filter.InvokeAsync(context, async _ =>
        {
            await fixture.WriteRowAsync(null);
            return Results.NoContent();
        });

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Null(await fixture.ReadRowAsync());
        Assert.Equal(expected, context.HttpContext.Response.Headers.ETag.ToString());
        Assert.NotEqual("\"AAAAAAAAAAA=\"", context.HttpContext.Response.Headers.ETag.ToString());
        fixture.AssertReads(2, CancellationToken.None);
    }

    [Theory]
    [InlineData("GET", null)]
    [InlineData("PATCH", null)]
    [InlineData("DELETE", "\"stale\"")]
    [InlineData("DELETE", "*")]
    public async Task RuntimeMissingRowsReachThe404HandlerWithoutCreatingAnEtag(string method, string? ifMatch)
    {
        await using var fixture = await Fixture.CreateAsync(true, null);
        var context = Context(method, fixture.Services);
        if (ifMatch is not null) context.HttpContext.Request.Headers.IfMatch = ifMatch;
        var handlerCalls = 0;

        var result = await fixture.Filter.InvokeAsync(context, _ =>
        {
            handlerCalls++;
            return ValueTask.FromResult<object?>(Results.NotFound());
        });

        Assert.Equal(1, handlerCalls);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey("ETag"));
        fixture.AssertReads(1, CancellationToken.None);
    }

    [Fact]
    public async Task RuntimeReadsForwardTheCurrentRequestTokenBeforeAndAfterTheHandler()
    {
        await using var fixture = await Fixture.CreateAsync(true, Row(null));
        using var before = new CancellationTokenSource();
        using var after = new CancellationTokenSource();
        var context = Context("GET", fixture.Services, before.Token);

        await fixture.Filter.InvokeAsync(context, _ =>
        {
            context.HttpContext.RequestAborted = after.Token;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        Assert.Equal(new[] { before.Token, after.Token }, fixture.ReadTokens);
        Assert.Equal(new[] { FilterId, FilterId }, fixture.ReadIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCancellationPropagatesBeforeOrAfterTheHandler(bool cancelAfterHandler)
    {
        await using var fixture = await Fixture.CreateAsync(true, Row(null));
        using var request = new CancellationTokenSource();
        if (!cancelAfterHandler) request.Cancel();
        var context = Context("GET", fixture.Services, request.Token);
        var handlerCalls = 0;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await fixture.Filter.InvokeAsync(context, _ =>
            {
                handlerCalls++;
                request.Cancel();
                return ValueTask.FromResult<object?>(Results.Ok());
            });
        });

        Assert.Equal(request.Token, exception.CancellationToken);
        Assert.Equal(cancelAfterHandler ? 1 : 0, handlerCalls);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey("ETag"));
        fixture.AssertReads(cancelAfterHandler ? 2 : 1, request.Token);
    }

    [Fact]
    public async Task RuntimeRoutesWithoutAnIntegerIdNeverReadPersistenceOrResolveRequestServices()
    {
        await using var fixture = await Fixture.CreateAsync(true, Row(null));
        foreach (var (method, path, routeId) in new (string, string, object?)[]
        {
            ("GET", "/v1/admin/ip-filters", null),
            ("GET", "/v1/admin/ip-filters/enabled", null),
            ("GET", "/v1/admin/ip-filters/settings", null),
            ("GET", "/v1/admin/ip-filters/check/127.0.0.1", null),
            ("GET", "/v1/admin/ip-filters/by-virtual-key/41", null),
            ("POST", "/v1/admin/ip-filters", null),
            ("GET", "/v1/admin/ip-filters/not-an-int", "not-an-int")
        })
        {
            var context = Context(method, fixture.Services, path: path, routeId: routeId);
            var expected = Results.Ok();
            var result = await fixture.Filter.InvokeAsync(context,
                _ => ValueTask.FromResult<object?>(expected));
            Assert.Same(expected, result);
            Assert.False(context.HttpContext.Response.Headers.ContainsKey("ETag"));
        }
        fixture.AssertReads(0, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeFactoryKeepsBothNonIpResourcesOnTheRealEfReader(bool virtualKey)
    {
        await using var database = new SqliteTestDatabase();
        await database.SeedAsync(async db =>
        {
            db.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = FilterId, GroupName = "non-IP version", UpdatedAt = OriginalTime
            });
            if (virtualKey)
                db.VirtualKeys.Add(new VirtualKey
                {
                    Id = FilterId, KeyName = "non-IP version", KeyHash = "non-IP hash",
                    VirtualKeyGroupId = FilterId, UpdatedAt = OriginalTime
                });
            await db.SaveChangesAsync();
        });
        var repository = new Mock<IIpFilterRepository>(MockBehavior.Strict);
        var filter = VersionedResourceEndpointFilter.CreateWithRuntimePersistence(repository.Object);
        await using var services = new ServiceCollection()
            .AddSingleton(database.CreateDbContextFactory()).BuildServiceProvider();
        var path = virtualKey ? $"/v1/admin/virtual-keys/{FilterId}" : $"/v1/admin/virtual-key-groups/{FilterId}";
        var context = Context("GET", services, path: path);

        var result = await filter.InvokeAsync(context,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(ExpectedTag(null, OriginalTime), context.HttpContext.Response.Headers.ETag.ToString());
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public void FactoryKeepsTheOriginalSolePublicParameterlessConstructorAndRejectsNull()
    {
        var constructor = Assert.Single(typeof(VersionedResourceEndpointFilter).GetConstructors());
        Assert.Empty(constructor.GetParameters());
        Assert.NotNull(new VersionedResourceEndpointFilter());
        var exception = Assert.Throws<ArgumentNullException>(() =>
            VersionedResourceEndpointFilter.CreateWithRuntimePersistence(null!));
        Assert.Equal("runtimeIpFilterRepository", exception.ParamName);
    }

    private static byte[]? Version(int kind) => kind switch
    {
        0 => [0, 1, 254, 255],
        1 => null,
        _ => []
    };

    private static string ExpectedTag(byte[]? version, DateTime time) =>
        $"\"{Convert.ToBase64String(version is { Length: > 0 } ? version : BitConverter.GetBytes(time.Ticks))}\"";

    private static IpFilterEntity Row(byte[]? version) => new()
    {
        Id = FilterId, FilterType = "whitelist", IpAddressOrCidr = "127.0.0.2",
        Name = "owned version fixture", Description = "original café 雪", IsEnabled = true,
        CreatedAt = OriginalTime.AddHours(-1), UpdatedAt = OriginalTime,
        CreatedBy = "creator", UpdatedBy = "editor", VirtualKeyId = null,
        RowVersion = version?.ToArray()
    };

    private static object? Fingerprint(IpFilterEntity? row) => row is null ? null :
        (row.Id, row.FilterType, row.IpAddressOrCidr, row.Name, row.Description, row.IsEnabled,
            row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy, row.VirtualKeyId,
            row.RowVersion is null ? null : Convert.ToHexString(row.RowVersion));

    private static IpFilterEntity? Clone(IpFilterEntity? row) => row is null ? null : new()
    {
        Id = row.Id, FilterType = row.FilterType, IpAddressOrCidr = row.IpAddressOrCidr,
        Name = row.Name, Description = row.Description, IsEnabled = row.IsEnabled,
        CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt, CreatedBy = row.CreatedBy,
        UpdatedBy = row.UpdatedBy, VirtualKeyId = row.VirtualKeyId, RowVersion = row.RowVersion?.ToArray()
    };

    private static TestContext Context(string method, IServiceProvider services,
        CancellationToken cancellationToken = default, string? path = null, object? routeId = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services, RequestAborted = cancellationToken };
        httpContext.Request.Method = method;
        httpContext.Request.Path = path ?? $"/v1/admin/ip-filters/{FilterId}";
        if (path is null || routeId is not null || path.EndsWith($"/{FilterId}", StringComparison.Ordinal))
            httpContext.Request.RouteValues["id"] = routeId ?? FilterId;
        return new TestContext(httpContext);
    }

    private sealed class TestContext(HttpContext context) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = context;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    private sealed class GuardedServices : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("Runtime IP version reads must not resolve request services.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteTestDatabase? _database;
        private readonly ServiceProvider? _services;
        private readonly Mock<IIpFilterRepository>? _repository;
        private IpFilterEntity? _row;
        public VersionedResourceEndpointFilter Filter { get; }
        public IServiceProvider Services { get; }
        public List<int> ReadIds { get; } = [];
        public List<CancellationToken> ReadTokens { get; } = [];

        private Fixture(IpFilterEntity? row)
        {
            _row = Clone(row);
            Services = new GuardedServices();
            _repository = new Mock<IIpFilterRepository>(MockBehavior.Strict);
            _repository.Setup(value => value.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int id, CancellationToken token) =>
                {
                    ReadIds.Add(id);
                    ReadTokens.Add(token);
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(id == FilterId ? Clone(_row) : null);
                });
            Filter = VersionedResourceEndpointFilter.CreateWithRuntimePersistence(_repository.Object);
        }

        private Fixture(SqliteTestDatabase database)
        {
            _database = database;
            _services = new ServiceCollection().AddSingleton(database.CreateDbContextFactory()).BuildServiceProvider();
            Services = _services;
            Filter = new VersionedResourceEndpointFilter();
        }

        public static async Task<Fixture> CreateAsync(bool runtime, IpFilterEntity? row)
        {
            if (runtime) return new Fixture(row);
            var database = new SqliteTestDatabase();
            try
            {
                if (row is not null)
                {
                    await database.SeedAsync(async db =>
                    {
                        db.IpFilters.Add(Clone(row)!);
                        await db.SaveChangesAsync();
                    });
                    await using var db = database.CreateContext();
                    // [Timestamp] is generated by EF; seed its actual persisted bytes independently.
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"IpFilters\" SET \"RowVersion\" = @version WHERE \"Id\" = @id",
                        new SqliteParameter("@version", SqliteType.Blob) { Value = (object?)row.RowVersion ?? DBNull.Value },
                        new SqliteParameter("@id", SqliteType.Integer) { Value = FilterId });
                }
                return new Fixture(database);
            }
            catch { await database.DisposeAsync(); throw; }
        }

        public async Task<IpFilterEntity?> ReadRowAsync()
        {
            if (_database is null) return Clone(_row);
            await using var db = _database.CreateContext();
            return await db.IpFilters.AsNoTracking().SingleOrDefaultAsync(value => value.Id == FilterId);
        }

        public async Task WriteRowAsync(IpFilterEntity? row)
        {
            if (_database is null) { _row = Clone(row); return; }
            await using var db = _database.CreateContext();
            if (row is null)
            {
                Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"IpFilters\" WHERE \"Id\" = @id",
                    new SqliteParameter("@id", SqliteType.Integer) { Value = FilterId }));
                return;
            }
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"IpFilters\" SET \"Description\" = @description, \"UpdatedAt\" = @updated, \"RowVersion\" = @version WHERE \"Id\" = @id",
                new SqliteParameter("@description", SqliteType.Text) { Value = (object?)row.Description ?? DBNull.Value },
                new SqliteParameter("@updated", SqliteType.Text) { Value = row.UpdatedAt },
                new SqliteParameter("@version", SqliteType.Blob) { Value = (object?)row.RowVersion ?? DBNull.Value },
                new SqliteParameter("@id", SqliteType.Integer) { Value = FilterId }));
        }

        public void ClearReads()
        {
            ReadIds.Clear();
            ReadTokens.Clear();
            _repository?.Invocations.Clear();
        }

        public void AssertReads(int count, CancellationToken token)
        {
            if (_repository is null) return;
            Assert.Equal(count, ReadIds.Count);
            Assert.All(ReadIds, id => Assert.Equal(FilterId, id));
            Assert.All(ReadTokens, value => Assert.Equal(token, value));
            _repository.Verify(value => value.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(count));
            _repository.VerifyNoOtherCalls();
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null) await _services.DisposeAsync();
            if (_database is not null) await _database.DisposeAsync();
        }
    }
}
