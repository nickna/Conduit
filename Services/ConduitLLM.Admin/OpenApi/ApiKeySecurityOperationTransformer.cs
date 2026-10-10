using ConduitLLM.Core.OpenApi;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>
/// Publishes the Admin API's security requirement from endpoint authorization metadata.
/// </summary>
public sealed class ApiKeySecurityOperationTransformer : AuthorizedOperationSecurityTransformer
{
    public const string SecuritySchemeName = "MasterKey";

    // SecurityService checks the master key on every published Admin route,
    // including routes without authorization metadata or marked AllowAnonymous.
    protected override bool MiddlewareRequiresAuthentication => true;

    public ApiKeySecurityOperationTransformer()
        : base(SecuritySchemeName)
    {
    }
}
