using ConduitLLM.Core.Models;
using ConduitLLM.Core.OpenApi;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.OpenApi;

namespace ConduitLLM.Gateway.OpenApi;

/// <summary>Normalizes Gateway media types, errors, binary bodies, and request IDs.</summary>
public sealed class ResponseContractOperationTransformer : IOpenApiOperationTransformer
{
    private static readonly HashSet<string> OpenAICompatibleOperations =
    [
        "Models_ListModels",
        "Models_RetrieveModel",
        "Embeddings_Create",
        "Chat_CreateCompletion",
        "Responses_Create",
        "Audio_CreateTranscription",
        "Audio_CreateSpeech",
        "Images_Create"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> BinaryContentTypes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Audio_CreateSpeech"] =
            [
                "audio/mpeg",
                "audio/opus",
                "audio/aac",
                "audio/flac",
                "audio/wav",
                "audio/pcm",
                "application/octet-stream"
            ],
            ["Downloads_Get"] = ["application/octet-stream"],
            ["Media_Get"] = ["application/octet-stream"]
        };

    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        Normalize(operation, context.Description.HttpMethod);

        if (operation.OperationId == "Media_Upload" &&
            operation.RequestBody?.Content?["multipart/form-data"].Schema is OpenApiSchema multipartSchema)
        {
            multipartSchema.Required?.Clear();
            operation.RequestBody.Description = "Optional media type (image/video/audio).";
        }

        if (operation.OperationId == "Audio_CreateTranscription" &&
            operation.RequestBody?.Content?["multipart/form-data"].Schema is OpenApiSchema transcriptionSchema)
        {
            // ASP.NET can emit unnamed primitive allOf branches for repeated string form
            // parameters. Describe each named form value from endpoint metadata instead,
            // retaining generated constraints for fields that already have object branches.
            var properties = new Dictionary<string, IOpenApiSchema>();
            foreach (var branch in transcriptionSchema.AllOf ?? [])
            {
                foreach (var property in branch.Properties ?? new Dictionary<string, IOpenApiSchema>())
                    properties[property.Key] = property.Value;
            }
            foreach (var property in transcriptionSchema.Properties ?? new Dictionary<string, IOpenApiSchema>())
                properties[property.Key] = property.Value;

            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in context.Description.ParameterDescriptions.Where(parameter =>
                parameter.Source == BindingSource.Form || parameter.Source == BindingSource.FormFile))
            {
                if (!properties.ContainsKey(parameter.Name))
                    properties[parameter.Name] = await context.GetOrCreateSchemaAsync(
                        parameter.Type, parameter, cancellationToken);
                if (parameter.IsRequired)
                    required.Add(parameter.Name);
            }

            transcriptionSchema.Type = JsonSchemaType.Object;
            transcriptionSchema.AllOf = null;
            transcriptionSchema.Properties = properties;
            transcriptionSchema.Required = required;
            foreach (var propertyName in new[]
            {
                "include",
                "known_speaker_names",
                "timestamp_granularities"
            })
            {
                transcriptionSchema.Properties[propertyName] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String }
                };
            }
            transcriptionSchema.Properties["known_speaker_references"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
            };
        }

        if (operation.Responses is null)
        {
            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(
            typeof(OpenAIErrorResponse),
            parameterDescription: null,
            cancellationToken);
        var document = context.Document ?? throw new InvalidOperationException("An OpenAPI document is required.");
        var components = document.Components ??= new OpenApiComponents();
        components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        components.Schemas["OpenAIErrorResponse"] = schema;
        var errorReference = new OpenApiSchemaReference("OpenAIErrorResponse", document);

        if (operation.OperationId is not null &&
            OpenAICompatibleOperations.Contains(operation.OperationId))
        {
            AddOpenAIErrorResponses(operation, errorReference);
        }

        foreach (var responseEntry in operation.Responses)
        {
            if (responseEntry.Value is not OpenApiResponse response)
            {
                continue;
            }

            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["x-request-id"] = new OpenApiHeader
            {
                Description = "Request identifier for support and distributed tracing.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            };

            if (int.TryParse(responseEntry.Key, out var status) && status >= 400)
            {
                response.Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = errorReference }
                };

                if (status is 429 or 503)
                {
                    response.Headers["Retry-After"] = new OpenApiHeader
                    {
                        Description = "Delay in seconds before retrying the request.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                }
            }
        }
    }

    internal static void Normalize(OpenApiOperation operation, string? httpMethod)
    {
        OpenApiMediaTypeNormalizer.RemoveLegacyAliases(operation.RequestBody?.Content);
        if (operation.Responses is null)
        {
            return;
        }

        foreach (var responseEntry in operation.Responses)
        {
            if (responseEntry.Value is not OpenApiResponse response)
            {
                continue;
            }

            OpenApiMediaTypeNormalizer.RemoveLegacyAliases(response.Content);
            if (!int.TryParse(responseEntry.Key, out var status) || status is < 200 or >= 300)
            {
                continue;
            }

            if (status is 204 or 205 || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                response.Content = new Dictionary<string, OpenApiMediaType>();
                continue;
            }

            if (operation.OperationId is not null &&
                BinaryContentTypes.TryGetValue(operation.OperationId, out var contentTypes))
            {
                response.Content = contentTypes.ToDictionary(
                    contentType => contentType,
                    _ => new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                    });
                continue;
            }

            if (responseEntry.Key == "200" &&
                operation.OperationId is "Chat_CreateCompletion" or "Responses_Create")
            {
                response.Content!["text/event-stream"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                };
            }
        }
    }

    private static void AddOpenAIErrorResponses(
        OpenApiOperation operation,
        IOpenApiSchema errorReference)
    {
        var descriptions = new Dictionary<string, string>
        {
            ["401"] = "Authentication failed.",
            ["403"] = "The authenticated key is not authorized for this operation.",
            ["408"] = "The request timed out.",
            ["413"] = "The request payload is too large.",
            ["429"] = "The request exceeded an applicable rate limit.",
            ["503"] = "The service is temporarily unavailable."
        };

        foreach (var (status, description) in descriptions)
        {
            if (operation.Responses!.ContainsKey(status))
                continue;

            operation.Responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = errorReference }
                }
            };
        }
    }
}
