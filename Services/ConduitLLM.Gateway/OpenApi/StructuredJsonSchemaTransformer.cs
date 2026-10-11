using System.Text.Json;
using System.Reflection;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Audio;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Gateway.OpenApi;

/// <summary>Describes structured JSON object dictionaries as open-ended objects for SDKs.</summary>
public sealed class StructuredJsonSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(Dictionary<string, JsonElement>) ||
            context.JsonPropertyInfo?.PropertyType == typeof(Dictionary<string, JsonElement>))
        {
            schema.Type = JsonSchemaType.Object;
            schema.AdditionalPropertiesAllowed = true;
            schema.AdditionalProperties = new OpenApiSchema();
        }

        if (IsProperty(context, typeof(EmbeddingRequest), nameof(EmbeddingRequest.Input)))
        {
            schema.OneOf =
            [
                new OpenApiSchema { Type = JsonSchemaType.String },
                ArrayOf(new OpenApiSchema { Type = JsonSchemaType.String }),
                ArrayOf(new OpenApiSchema { Type = JsonSchemaType.Integer }),
                ArrayOf(ArrayOf(new OpenApiSchema { Type = JsonSchemaType.Integer }))
            ];
        }
        else if (IsProperty(context, typeof(Message), nameof(Message.Content)))
        {
            schema.Type = null;
            schema.OneOf = ChatUnionSchemas.Content().OneOf;
        }
        else if (IsProperty(context, typeof(ChatCompletionRequest), nameof(ChatCompletionRequest.Stop)))
        {
            schema.OneOf =
            [
                new OpenApiSchema { Type = JsonSchemaType.String },
                ArrayOf(new OpenApiSchema { Type = JsonSchemaType.String })
            ];
            schema.Type = null;
            schema.Items = null;
        }

        if (context.JsonTypeInfo.Type == typeof(ToolChoice))
        {
            schema.Type = null;
            schema.OneOf = ChatUnionSchemas.ToolChoice().OneOf;
        }

        if (context.JsonTypeInfo.Type == typeof(ChatCompletionRequest))
        {
            RemoveProperties(schema,
                "top_k",
                "reasoning",
                "session_id",
                "function_configuration_ids",
                "enable_agentic_mode",
                "max_agentic_iterations");
        }
        else if (context.JsonTypeInfo.Type == typeof(ChatCompletionResponse))
        {
            RemoveProperties(schema, "agentic_metrics", "performance_metrics", "seed");
        }
        else if (context.JsonTypeInfo.Type == typeof(ImageGenerationRequest))
        {
            RemoveProperties(schema, "image", "mask", "operation");
        }
        else if (context.JsonTypeInfo.Type == typeof(ImageGenerationResponse))
        {
            schema.Required?.Remove("data");
        }
        else if (context.JsonTypeInfo.Type == typeof(AudioTranscriptionResponse))
        {
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("duration");
            schema.Required.Add("language");
            schema.Required.Add("segments");
            schema.Required.Add("task");
        }

        return Task.CompletedTask;
    }

    private static void RemoveProperties(OpenApiSchema schema, params string[] propertyNames)
    {
        if (schema.Properties is null)
            return;

        foreach (var propertyName in propertyNames)
        {
            schema.Properties.Remove(propertyName);
            schema.Required?.Remove(propertyName);
        }
    }

    private static bool IsProperty(
        OpenApiSchemaTransformerContext context,
        Type declaringType,
        string propertyName) =>
        context.JsonPropertyInfo?.AttributeProvider is PropertyInfo property &&
        property.DeclaringType == declaringType &&
        property.Name == propertyName;

    private static OpenApiSchema ArrayOf(IOpenApiSchema items) =>
        new() { Type = JsonSchemaType.Array, Items = items };
}
