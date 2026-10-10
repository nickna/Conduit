using ConduitLLM.Admin.Auditing;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.ModelCatalogs;
using ConduitLLM.Core.Events;

namespace ConduitLLM.Admin.Endpoints;

public static class BundledModelCatalogEndpoints
{
    public static IEndpointRouteBuilder MapBundledModelCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/model-catalogs")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("BundledModelCatalog");

        group.MapPost("/import", Import)
            .WithName("BundledModelCatalog_Import")
            .WithSummary("Import the bundled model catalog")
            .Produces<BundledModelCatalogImportResult>();
        return app;
    }

    private static async Task<IResult> Import(
        [Microsoft.AspNetCore.Mvc.FromServices] IBundledModelCatalogImporter importer,
        [Microsoft.AspNetCore.Mvc.FromServices] IEventBus eventBus,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var result = await importer.ImportAsync(false, cancellationToken)
            ?? throw new InvalidOperationException("A forced bundled catalog import unexpectedly returned no result.");

        if (result.Created.Identifiers > 0)
        {
            await eventBus.PublishAsync(new DiscoveryCacheInvalidationRequested
            {
                Reason = "Bundled provider model catalog imported",
                RequestedBy = "Admin User",
                CorrelationId = Guid.NewGuid().ToString()
            });
        }

        AdminAudit.Log(
            httpContext,
            loggerFactory.CreateLogger("ConduitLLM.Admin.Endpoints.BundledModelCatalog"),
            "Imported",
            "BundledModelCatalog",
            detail: $"Providers: {result.ProvidersProcessed}, identifiers created: {result.Created.Identifiers}, " +
                    $"skipped: {result.SkippedExistingIdentifiers}, conflicts: {result.Conflicts.Count}");
        return Results.Ok(result);
    }
}
