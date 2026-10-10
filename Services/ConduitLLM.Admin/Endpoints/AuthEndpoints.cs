using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.Models;
using ConduitLLM.Admin.Services;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/auth-tokens")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Auth");
        group.MapPost("/ephemeral-master-key", Generate)
            .WithName("Auth_GenerateEphemeralMasterKey")
            .WithSummary("Generate an ephemeral master key")
            .Produces<EphemeralMasterKeyResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);
        return app;
    }

    private static async Task<IResult> Generate(
        [FromServices] IEphemeralMasterKeyService service,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var response = await service.CreateEphemeralMasterKeyAsync();
        AdminAudit.Log(context, loggerFactory.CreateLogger("ConduitLLM.Admin.Endpoints.Auth"),
            "Generated", "EphemeralMasterKey", detail: $"TTL: {response.ExpiresInSeconds}s");
        return Results.Ok(response);
    }
}
