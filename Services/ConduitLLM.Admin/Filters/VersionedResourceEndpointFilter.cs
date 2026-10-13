using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Utilities;
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

        if (context.HttpContext.Request.Method is not ("GET" or "HEAD"))
        {
            var current = await ReadVersionAsync(dbContextFactory, context.HttpContext.Request.Path,
                id, context.HttpContext.RequestAborted);
            var candidates = context.HttpContext.Request.Headers.IfMatch;
            if (current is not null && StringValues.IsNullOrEmpty(candidates))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status428PreconditionRequired,
                    title: "If-Match is required",
                    detail: "Fetch the resource and send its ETag in the If-Match header.");
            }

            if (current is not null && !new ResourcePrecondition(candidates).Matches(current))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    title: "Resource version mismatch",
                    detail: "The resource changed after it was read. Fetch it again and retry.");
            }
        }

        try
        {
            var result = await next(context);
            if (result is IValueHttpResult { Value: IVersionedRepresentation { ResourceETag: { } etag } })
                context.HttpContext.Response.Headers.ETag = etag;
            return result;
        }
        catch (ResourcePreconditionRequiredException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired,
                title: "If-Match is required", detail: exception.Message);
        }
        catch (ResourcePreconditionFailedException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Resource version mismatch", detail: exception.Message);
        }
    }

    public static ResourcePrecondition? ReadPrecondition(HttpContext context) =>
        context.RequestServices.GetService<IDbContextFactory<ConduitDbContext>>() is null
            ? null : new ResourcePrecondition(context.Request.Headers.IfMatch);

    private static async Task<ResourceVersion?> ReadVersionAsync(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        PathString path,
        int id,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (path.StartsWithSegments("/v1/admin/virtual-keys"))
            return await ResourceMutationGuard.ReadVersionAsync<VirtualKey>(db, id, cancellationToken);
        if (path.StartsWithSegments("/v1/admin/virtual-key-groups"))
            return await ResourceMutationGuard.ReadVersionAsync<VirtualKeyGroup>(db, id, cancellationToken);
        if (path.StartsWithSegments("/v1/admin/ip-filters"))
            return await ResourceMutationGuard.ReadVersionAsync<IpFilterEntity>(db, id, cancellationToken);
        return null;
    }
}
