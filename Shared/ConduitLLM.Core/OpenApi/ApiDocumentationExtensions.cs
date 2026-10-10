using Microsoft.AspNetCore.Builder;

namespace ConduitLLM.Core.OpenApi;

/// <summary>Attaches parameter documentation to Minimal API delegates without runtime reflection.</summary>
public static class ApiDocumentationExtensions
{
    public static RouteHandlerBuilder WithParameterDescription(
        this RouteHandlerBuilder builder, string name, string description) =>
        builder.WithMetadata(new EndpointParameterDescription(name, description));
}

public sealed record EndpointParameterDescription(string Name, string Description);
