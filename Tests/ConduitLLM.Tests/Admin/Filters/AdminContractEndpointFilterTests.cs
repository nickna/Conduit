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

    [Fact]
    public async Task VersionFilter_EmitsStrongEtagAndAcceptsMatchingOrWildcardPreconditions()
    {
        await using var database = new SqliteTestDatabase();
        var updatedAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        await database.SeedAsync(async db =>
        {
            db.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = 41,
                GroupName = "etag-test",
                UpdatedAt = updatedAt
            });
            await db.SaveChangesAsync();
        });

        var factory = database.CreateDbContextFactory();
        var filter = new VersionedResourceEndpointFilter();
        var expected = $"\"{Convert.ToBase64String(BitConverter.GetBytes(updatedAt.Ticks))}\"";
        var get = Context("GET", "/v1/admin/virtual-key-groups/41", 41, factory);

        var result = await filter.InvokeAsync(
            get,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(expected, get.HttpContext.Response.Headers.ETag);

        foreach (var candidate in new[] { expected, "*" })
        {
            var patch = Context("PATCH", "/v1/admin/virtual-key-groups/41", 41, factory);
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

    [Theory]
    [InlineData("/v1/admin/virtual-keys/999", "GET", null)]
    [InlineData("/v1/admin/virtual-keys/999", "PATCH", null)]
    [InlineData("/v1/admin/virtual-keys/999", "DELETE", null)]
    [InlineData("/v1/admin/virtual-keys/999", "PATCH", "\"stale\"")]
    [InlineData("/v1/admin/virtual-keys/999", "DELETE", "*")]
    [InlineData("/v1/admin/virtual-key-groups/999", "GET", null)]
    [InlineData("/v1/admin/virtual-key-groups/999", "PATCH", null)]
    [InlineData("/v1/admin/virtual-key-groups/999", "DELETE", null)]
    [InlineData("/v1/admin/virtual-key-groups/999", "PATCH", "\"stale\"")]
    [InlineData("/v1/admin/virtual-key-groups/999", "DELETE", "*")]
    [InlineData("/v1/admin/ip-filters/999", "GET", null)]
    [InlineData("/v1/admin/ip-filters/999", "PATCH", null)]
    [InlineData("/v1/admin/ip-filters/999", "DELETE", null)]
    [InlineData("/v1/admin/ip-filters/999", "PATCH", "\"stale\"")]
    [InlineData("/v1/admin/ip-filters/999", "DELETE", "*")]
    public async Task VersionFilter_MissingResourcesReachHandlerWithoutSyntheticVersion(
        string path, string method, string? ifMatch)
    {
        await using var database = new SqliteTestDatabase();
        var context = Context(method, path, 999, database.CreateDbContextFactory());
        if (ifMatch is not null) context.HttpContext.Request.Headers.IfMatch = ifMatch;
        var called = false;
        var result = await new VersionedResourceEndpointFilter().InvokeAsync(context, _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(Results.NotFound());
        });
        Assert.True(called);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey("ETag"));
    }

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
