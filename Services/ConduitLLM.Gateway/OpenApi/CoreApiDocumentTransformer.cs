using ConduitLLM.Core.OpenApi;

using Microsoft.OpenApi;

namespace ConduitLLM.Gateway.OpenApi;

/// <summary>
/// Transforms the OpenAPI document for the Gateway API
/// </summary>
public sealed class CoreApiDocumentTransformer : ApiDocumentTransformer
{
    protected override IReadOnlyDictionary<string, string> TagDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Models"] = "Model aliases and their metadata.",
            ["Authentication"] = "Short-lived virtual-key credentials.",
            ["Rerank"] = "Relevance ranking of documents against a query.",
            ["Embeddings"] = "Vector embeddings for text input.",
            ["Chat"] = "Chat completions, including streamed responses and function calling.",
            ["Responses"] = "Stateless OpenAI-compatible model responses and streamed events.",
            ["Discovery"] = "Available model and function capabilities and parameter definitions.",
            ["Tasks"] = "Status, polling, and cancellation of asynchronous tasks owned by the virtual key.",
            ["Functions"] = "Function execution and retrieval of execution results.",
            ["Audio"] = "Audio transcription and text-to-speech generation.",
            ["Media"] = "Media uploads, metadata, and retrieval by storage key.",
            ["Downloads"] = "File downloads, metadata, and download URL generation.",
            ["Images"] = "Synchronous and asynchronous image generation and task management.",
            ["Videos"] = "Asynchronous video generation, status, retries, and cancellation."
        };

    public CoreApiDocumentTransformer()
        : base(
            "Conduit Gateway API",
            "OpenAI-compatible API for multi-provider LLM access - Requires Bearer token authentication",
            VirtualKeySecurityOperationTransformer.SecuritySchemeName,
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "opaque virtual key",
                Description = "ConduitLLM virtual key supplied as an opaque Bearer token."
            })
    {
    }
}
