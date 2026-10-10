using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Core.OpenApi;

/// <summary>Adds a configured security scheme to endpoints that require authorization.</summary>
public abstract class AuthorizedOperationSecurityTransformer : IOpenApiOperationTransformer
{
    private readonly string _securitySchemeName;

    protected virtual bool MiddlewareRequiresAuthentication => false;

    protected AuthorizedOperationSecurityTransformer(string securitySchemeName)
    {
        _securitySchemeName = securitySchemeName;
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        operation.Security = new List<OpenApiSecurityRequirement>();

        if (!MiddlewareRequiresAuthentication &&
            (metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any()))
        {
            return Task.CompletedTask;
        }

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(_securitySchemeName, context.Document, null)] = []
        });

        return Task.CompletedTask;
    }
}
