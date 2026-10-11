using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ConduitLLM.Gateway.OpenApi;

/// <summary>The supported chat unions from the pinned OpenAI contract.</summary>
internal static class ChatUnionSchemas
{
    public static OpenApiSchema Content(OpenApiDocument? document = null, bool providerExtensions = false)
    {
        var parts = new List<IOpenApiSchema>
        {
            Part("text", "text", String()),
            Part("image_url", "image_url", Object(new()
            {
                ["url"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                ["detail"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String, Enum = Values("auto", "low", "high"),
                    Default = JsonValue.Create("auto")
                }
            }, "url")),
            Part("input_audio", "input_audio", Object(new()
            {
                ["data"] = String(), ["format"] = providerExtensions
                    ? String("wav", "mp3", "aiff", "aac", "ogg", "flac", "m4a", "pcm16", "pcm24")
                    : String("wav", "mp3")
            }, "data", "format")),
            Part("file", "file", Object(new()
            {
                ["filename"] = String(), ["file_data"] = String(), ["file_id"] = String()
            })),
            Part("refusal", "refusal", String(), cacheBreakpoint: false)
        };
        var names = new List<string> { "ChatTextContentPart", "ChatImageContentPart", providerExtensions ? "ProviderChatAudioContentPart" : "ChatAudioContentPart", "ChatFileContentPart", "ChatRefusalContentPart" };
        var types = new List<string> { "text", "image_url", "input_audio", "file", "refusal" };
        if (providerExtensions)
        {
            parts.Add(Part("video_url", "video_url", Object(new()
            {
                ["url"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                ["detail"] = String("auto", "low", "high"),
                ["max_frames"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["sample_rate"] = new OpenApiSchema { Type = JsonSchemaType.Number },
                ["start_time"] = new OpenApiSchema { Type = JsonSchemaType.Number },
                ["end_time"] = new OpenApiSchema { Type = JsonSchemaType.Number }
            }, "url"), cacheBreakpoint: false));
            names.Add("ProviderChatVideoContentPart");
            types.Add("video_url");
        }
        var mapping = new Dictionary<string, OpenApiSchemaReference>();
        if (document is not null)
        {
            var schemas = (document.Components ??= new OpenApiComponents()).Schemas ??= new Dictionary<string, IOpenApiSchema>();
            for (var index = 0; index < parts.Count; index++)
            {
                schemas[names[index]] = parts[index];
                var reference = new OpenApiSchemaReference(names[index], document);
                parts[index] = reference;
                mapping[types[index]] = reference;
            }
        }
        IOpenApiSchema items = new OpenApiSchema
        {
            OneOf = parts,
            Discriminator = new OpenApiDiscriminator { PropertyName = "type", Mapping = mapping }
        };
        if (providerExtensions)
        {
            var providerPart = new OpenApiSchema
            {
                Title = "Provider content part",
                Description = "Additional provider-specific content types. Only available on the Conduit chat route; the configured provider determines support.",
                Type = JsonSchemaType.Object,
                Required = new HashSet<string> { "type" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["type"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String, MinLength = 1,
                        Not = String(types.ToArray())
                    }
                },
                AdditionalPropertiesAllowed = true
            };
            // A catch-all provider discriminator cannot be selected by an OpenAPI
            // discriminator mapping. Keep it outside the closed OpenAI union.
            items = new OpenApiSchema { OneOf = [items, providerPart] };
        }
        return new OpenApiSchema
        {
            OneOf =
            [
                new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Array, MinItems = 1,
                    Items = items
                }
            ]
        };
    }

    public static OpenApiSchema ToolChoice() => new()
    {
        OneOf =
        [
            String("none", "auto", "required"),
            Object(new()
            {
                ["type"] = String("function"),
                ["function"] = Object(new()
                {
                    ["name"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1 }
                }, "name")
            }, "type", "function")
        ]
    };

    private static OpenApiSchema Part(string type, string field, IOpenApiSchema value, bool cacheBreakpoint = true)
    {
        var properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = String(type), [field] = value
        };
        if (cacheBreakpoint)
            properties["prompt_cache_breakpoint"] = Object(new() { ["mode"] = String("explicit") }, "mode");
        return Object(properties, "type", field);
    }

    private static OpenApiSchema Object(Dictionary<string, IOpenApiSchema> properties, params string[] required) =>
        new() { Type = JsonSchemaType.Object, Properties = properties, Required = required.ToHashSet() };

    private static OpenApiSchema String(params string[] values) =>
        new() { Type = JsonSchemaType.String, Enum = values.Length == 0 ? null : Values(values) };

    private static List<JsonNode> Values(params string[] values) =>
        values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
}
