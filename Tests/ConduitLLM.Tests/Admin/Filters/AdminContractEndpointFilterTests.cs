using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Filters;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Admin.Filters;

public sealed class AdminContractEndpointFilterTests
{
    [Fact]
    public async Task CollectionFilter_UsesCanonicalPaginationAndHandlesMaximumPage()
    {
        var filter = new OperationLoggingEndpointFilter(
            NullLogger<OperationLoggingEndpointFilter>.Instance);
        var context = Context("GET", "/v1/admin/example");
        context.HttpContext.Request.QueryString = new QueryString("?page=2&pageSize=2");

        var result = await filter.InvokeAsync(
            context,
            _ => ValueTask.FromResult<object?>(Results.Ok(new[] { 1, 2, 3, 4, 5 })));

        var page = Assert.IsType<PagedResult<object?>>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(new object?[] { 3, 4 }, page.Data);
        Assert.Equal(2, page.Pagination.Page);
        Assert.Equal(2, page.Pagination.PageSize);
        Assert.Equal(5, page.Pagination.TotalItems);
        Assert.Equal(3, page.Pagination.TotalPages);

        context = Context("GET", "/v1/admin/example");
        context.HttpContext.Request.QueryString =
            new QueryString($"?page={int.MaxValue}&pageSize=1000");

        result = await filter.InvokeAsync(
            context,
            _ => ValueTask.FromResult<object?>(Results.Ok(new[] { 1, 2, 3 })));

        page = Assert.IsType<PagedResult<object?>>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Empty(page.Data);
        Assert.Equal(int.MaxValue, page.Pagination.Page);
        Assert.Equal(100, page.Pagination.PageSize);
    }

    [Theory]
    [InlineData("/v1/admin/provider-errors/recent")]
    [InlineData("/v1/admin/providers/settings-schema")]
    public async Task CollectionFilter_DoesNotWrapDocumentedArrayResponses(string path)
    {
        var filter = new OperationLoggingEndpointFilter(
            NullLogger<OperationLoggingEndpointFilter>.Instance);
        var original = Results.Ok(new[] { 1, 2, 3 });

        var result = await filter.InvokeAsync(
            Context("GET", path),
            _ => ValueTask.FromResult<object?>(original));

        Assert.Same(original, result);
    }

    [Theory]
    [InlineData("application/merge-patch+json", true)]
    [InlineData("application/merge-patch+json; charset=utf-8", true)]
    [InlineData("application/json", false)]
    public async Task MergePatchFilter_RequiresTheRfc7386MediaType(
        string contentType,
        bool shouldContinue)
    {
        var context = Context("PATCH", "/v1/admin/example");
        context.HttpContext.Request.ContentType = contentType;
        var called = false;

        var result = await new JsonMergePatchContentTypeEndpointFilter()
            .InvokeAsync(context, _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        Assert.Equal(shouldContinue, called);
        Assert.Equal(
            shouldContinue ? StatusCodes.Status200OK : StatusCodes.Status415UnsupportedMediaType,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("virtual-keys", false)]
    [InlineData("virtual-keys", true)]
    [InlineData("virtual-key-groups", false)]
    [InlineData("virtual-key-groups", true)]
    [InlineData("ip-filters", false)]
    [InlineData("ip-filters", true)]
    public async Task VersionFilter_EmitsStrongEtagAndAcceptsMatchingOrWildcardPreconditions(
        string resource, bool useRowVersion)
    {
        await using var database = new SqliteTestDatabase();
        var updatedAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        byte[]? rowVersion = useRowVersion ? new byte[] { 1, 2, 3, 4 } : null;
        await SeedResourceAsync(database, resource, 41, updatedAt, rowVersion);

        var factory = database.CreateDbContextFactory();
        var filter = new VersionedResourceEndpointFilter();
        var expected = $"\"{Convert.ToBase64String(rowVersion ?? BitConverter.GetBytes(updatedAt.Ticks))}\"";
        var get = Context("GET", $"/v1/admin/{resource}/41", 41, factory);

        var result = await filter.InvokeAsync(
            get,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(expected, get.HttpContext.Response.Headers.ETag);

        foreach (var candidate in new[] { expected, "*", $"\"stale\", {expected}" })
        {
            var patch = Context("PATCH", $"/v1/admin/{resource}/41", 41, factory);
            patch.HttpContext.Request.Headers.IfMatch = candidate;
            var called = false;

            result = await filter.InvokeAsync(patch, _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

            Assert.True(called);
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Equal(expected, patch.HttpContext.Response.Headers.ETag);
        }
    }

    [Theory]
    [InlineData(null, StatusCodes.Status428PreconditionRequired)]
    [InlineData("\"stale\"", StatusCodes.Status412PreconditionFailed)]
    public async Task VersionFilter_RejectsMissingOrStalePreconditions(
        string? ifMatch,
        int expectedStatus)
    {
        await using var database = new SqliteTestDatabase();
        await database.SeedAsync(async db =>
        {
            db.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = 42,
                GroupName = "precondition-test"
            });
            await db.SaveChangesAsync();
        });

        var factory = database.CreateDbContextFactory();
        var context = Context("DELETE", "/v1/admin/virtual-key-groups/42", 42, factory);
        if (ifMatch is not null)
            context.HttpContext.Request.Headers.IfMatch = ifMatch;
        var called = false;

        var result = await new VersionedResourceEndpointFilter()
            .InvokeAsync(context, _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(Results.NoContent());
            });

        Assert.False(called);
        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    public static IEnumerable<object?[]> MissingResourceRequests()
    {
        foreach (var resource in new[] { "virtual-keys", "virtual-key-groups", "ip-filters" })
        {
            yield return new object?[] { resource, "GET", null };
            yield return new object?[] { resource, "HEAD", null };
            foreach (var method in new[] { "PATCH", "DELETE" })
            {
                foreach (var ifMatch in new[] { null, "\"stale\"", "*" })
                    yield return new object?[] { resource, method, ifMatch };
            }
        }
    }

    [Theory]
    [MemberData(nameof(MissingResourceRequests))]
    public async Task VersionFilter_MissingResourcesReachHandlerWithoutSyntheticVersion(
        string resource, string method, string? ifMatch)
    {
        await using var database = new SqliteTestDatabase();
        var context = Context(method, $"/v1/admin/{resource}/404", 404,
            database.CreateDbContextFactory());
        if (ifMatch is not null)
            context.HttpContext.Request.Headers.IfMatch = ifMatch;
        var called = false;

        var result = await new VersionedResourceEndpointFilter().InvokeAsync(context, _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(Results.NotFound());
        });

        Assert.True(called);
        Assert.Equal(StatusCodes.Status404NotFound,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, context.HttpContext.Response.Headers.ETag.Count);
    }

    [Theory]
    [InlineData("virtual-keys")]
    [InlineData("virtual-key-groups")]
    [InlineData("ip-filters")]
    public async Task VersionFilter_DeletedResourceRetainsItsOriginalEtag(string resource)
    {
        await using var database = new SqliteTestDatabase();
        var updatedAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        await SeedResourceAsync(database, resource, 43, updatedAt);
        var context = Context("DELETE", $"/v1/admin/{resource}/43", 43,
            database.CreateDbContextFactory());
        var expected = $"\"{Convert.ToBase64String(BitConverter.GetBytes(updatedAt.Ticks))}\"";
        context.HttpContext.Request.Headers.IfMatch = expected;

        var result = await new VersionedResourceEndpointFilter().InvokeAsync(context, async _ =>
        {
            await using var db = database.CreateContext();
            switch (resource)
            {
                case "virtual-keys":
                    await db.VirtualKeys.Where(key => key.Id == 43).ExecuteDeleteAsync();
                    break;
                case "virtual-key-groups":
                    await db.VirtualKeyGroups.Where(group => group.Id == 43).ExecuteDeleteAsync();
                    break;
                case "ip-filters":
                    await db.IpFilters.Where(filter => filter.Id == 43).ExecuteDeleteAsync();
                    break;
            }
            return Results.NoContent();
        });

        Assert.Equal(StatusCodes.Status204NoContent,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(expected, context.HttpContext.Response.Headers.ETag);
    }

    private static Task SeedResourceAsync(SqliteTestDatabase database, string resource,
        int id, DateTime updatedAt, byte[]? rowVersion = null) => database.SeedAsync(async db =>
        {
            db.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = id, GroupName = "etag-test", UpdatedAt = updatedAt, RowVersion = rowVersion
            });
            if (resource == "virtual-keys")
                db.VirtualKeys.Add(new VirtualKey
                {
                    Id = id, KeyName = "etag-test", KeyHash = "etag-test-hash",
                    VirtualKeyGroupId = id, UpdatedAt = updatedAt, RowVersion = rowVersion
                });
            if (resource == "ip-filters")
                db.IpFilters.Add(new IpFilterEntity
                {
                    Id = id, IpAddressOrCidr = "192.0.2.1", UpdatedAt = updatedAt, RowVersion = rowVersion
                });
            await db.SaveChangesAsync();
            // SQLite has no database-generated rowversion. Seed the stored bytes explicitly.
            if (rowVersion is not null)
            {
                switch (resource)
                {
                    case "virtual-keys":
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE \"VirtualKeys\" SET \"RowVersion\" = {rowVersion} WHERE \"Id\" = {id}");
                        break;
                    case "virtual-key-groups":
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE \"VirtualKeyGroups\" SET \"RowVersion\" = {rowVersion} WHERE \"Id\" = {id}");
                        break;
                    case "ip-filters":
                        await db.Database.ExecuteSqlRawAsync(
                            "UPDATE \"IpFilters\" SET \"RowVersion\" = {0} WHERE \"Id\" = {1}",
                            rowVersion, id);
                        break;
                }
            }
        });

    private static TestInvocationContext Context(
        string method,
        string path,
        int? id = null,
        IDbContextFactory<ConduitDbContext>? dbContextFactory = null)
    {
        var httpContext = new DefaultHttpContext();
        if (dbContextFactory is not null)
        {
            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton(dbContextFactory)
                .BuildServiceProvider();
        }
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        if (id.HasValue)
            httpContext.Request.RouteValues["id"] = id.Value;
        return new TestInvocationContext(httpContext);
    }

    private sealed class TestInvocationContext(HttpContext httpContext)
        : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }
}
