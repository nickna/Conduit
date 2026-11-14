using Microsoft.OpenApi;

namespace ConduitLLM.Core.OpenApi;

public static class OpenApiMediaTypeNormalizer
{
    public static OpenApiMediaType CopyForMutation(IOpenApiMediaType mediaType) =>
        mediaType.CreateShallowCopy() as OpenApiMediaType
        ?? throw new InvalidOperationException("OpenAPI 3.1 transforms require inline media types.");

    public static void RemoveLegacyAliases(IDictionary<string, IOpenApiMediaType>? content)
    {
        content?.Remove("text/json");
        content?.Remove("text/plain");
        content?.Remove("application/*+json");
    }
}
