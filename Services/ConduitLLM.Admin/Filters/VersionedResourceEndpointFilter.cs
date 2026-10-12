using ConduitLLM.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace ConduitLLM.Admin.Filters;

/// <summary>Applies conditional-request semantics to resources backed by row versions.</summary>
public sealed class VersionedResourceEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var dbContextFactory = context.HttpContext.RequestServices
            .GetService<IDbContextFactory<ConduitDbContext>>();
        if (dbContextFactory is null)
            return await next(context);

        if (!int.TryParse(context.HttpContext.Request.RouteValues["id"]?.ToString(), out var id))
            return await next(context);

        var current = await ReadVersionAsync(
            dbContextFactory,
            context.HttpContext.Request.Path,
            id,
            context.HttpContext.RequestAborted);
        if (current is null)
            return await next(context);

        if (context.HttpContext.Request.Method is not ("GET" or "HEAD"))
        {
            var candidates = context.HttpContext.Request.Headers.IfMatch;
            if (StringValues.IsNullOrEmpty(candidates))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status428PreconditionRequired,
                    title: "If-Match is required",
                    detail: "Fetch the resource and send its ETag in the If-Match header.");
            }

            if (!Matches(candidates, current.Value.ETag))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    title: "Resource version mismatch",
                    detail: "The resource changed after it was read. Fetch it again and retry.");
            }
        }

        var result = await next(context);
        var updated = await ReadVersionAsync(
            dbContextFactory,
            context.HttpContext.Request.Path,
            id,
            context.HttpContext.RequestAborted);
        context.HttpContext.Response.Headers.ETag = updated?.ETag ?? current.Value.ETag;
        return result;
    }

    private static async Task<ResourceVersion?> ReadVersionAsync(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        PathString path,
        int id,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (path.StartsWithSegments("/v1/admin/virtual-keys"))
        {
            return await db.VirtualKeys.AsNoTracking()
                .Where(entity => entity.Id == id)
                .Select(entity => (ResourceVersion?)new ResourceVersion(entity.RowVersion, entity.UpdatedAt))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (path.StartsWithSegments("/v1/admin/virtual-key-groups"))
        {
            return await db.VirtualKeyGroups.AsNoTracking()
                .Where(entity => entity.Id == id)
                .Select(entity => (ResourceVersion?)new ResourceVersion(entity.RowVersion, entity.UpdatedAt))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (path.StartsWithSegments("/v1/admin/ip-filters"))
        {
            return await db.IpFilters.AsNoTracking()
                .Where(entity => entity.Id == id)
                .Select(entity => (ResourceVersion?)new ResourceVersion(entity.RowVersion, entity.UpdatedAt))
                .SingleOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private static bool Matches(StringValues candidates, string current) =>
        candidates.SelectMany(value => value?.Split(',') ?? [])
            .Select(value => value.Trim())
            .Any(value => value == "*" || string.Equals(value, current, StringComparison.Ordinal));

    private readonly record struct ResourceVersion(byte[]? RowVersion, DateTime UpdatedAt)
    {
        public string ETag => $"\"{Convert.ToBase64String(RowVersion is { Length: > 0 } ? RowVersion : BitConverter.GetBytes(UpdatedAt.Ticks))}\"";
    }
}
