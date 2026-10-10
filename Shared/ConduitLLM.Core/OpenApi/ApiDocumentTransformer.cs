using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Core.OpenApi;

/// <summary>Sets service metadata and registers its authentication scheme.</summary>
public abstract class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly string _title;
    private readonly string _description;
    private readonly string _securitySchemeName;
    private readonly OpenApiSecurityScheme _securityScheme;

    protected abstract IReadOnlyDictionary<string, string> TagDescriptions { get; }

    protected ApiDocumentTransformer(
        string title,
        string description,
        string securitySchemeName,
        OpenApiSecurityScheme securityScheme)
    {
        _title = title;
        _description = description;
        _securitySchemeName = securitySchemeName;
        _securityScheme = securityScheme;
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = _title;
        document.Info.Version = "v1";
        document.Info.Description = _description;
        document.Info.Contact = new OpenApiContact
        {
            Name = "Conduit maintainers",
            Url = new Uri("https://github.com/nickna/Conduit/issues")
        };
        document.Info.License = new OpenApiLicense
        {
            Name = "MIT",
            Identifier = "MIT"
        };
        // A relative server keeps the contract usable for self-hosted deployments.
        document.Servers = [new OpenApiServer { Url = "/", Description = "This Conduit deployment" }];
        foreach (var tag in (document.Tags ?? new HashSet<OpenApiTag>())
            .Where(tag => tag.Name is not null && TagDescriptions.ContainsKey(tag.Name)))
        {
            tag.Description = TagDescriptions[tag.Name!];
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[_securitySchemeName] = _securityScheme;

        return Task.CompletedTask;
    }
}
