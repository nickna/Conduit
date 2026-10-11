using System.Text.Json;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using Microsoft.AspNetCore.Routing;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>Validates chat content before rate limiting, billing, or provider invocation.</summary>
public sealed class ChatContentValidationFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<ChatCompletionRequest>().Single();
        var providerExtensions = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<IEndpointNameMetadata>()?.EndpointName == "Chat_CreateEnhancedCompletion";
        if (request.Messages is null)
            throw new InvalidRequestException("Messages must be an array.", "invalid_chat_content", "messages");
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            if (message is null)
                throw new InvalidRequestException("A message must be an object.", "invalid_chat_content", $"messages[{index}]");
            if (message.Content is null)
                continue;
            var content = message.Content is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(message.Content, CoreHttpJsonContext.Default.Object);
            ValidateContent(content, message.Role, $"messages[{index}].content", providerExtensions);
        }
        return next(context);
    }

    private static void ValidateContent(JsonElement content, string role, string path, bool providerExtensions)
    {
        if (content.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            return;
        Require(content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0,
            path, "Content must be a string, null, or a nonempty array of content parts.");
        var index = 0;
        foreach (var part in content.EnumerateArray())
        {
            var partPath = $"{path}[{index++}]";
            Require(part.ValueKind == JsonValueKind.Object, partPath, "A content part must be an object.");
            var type = RequiredString(part, "type", partPath);
            Require(type.Length > 0, $"{partPath}.type", "A content part needs a nonempty type discriminator.");
            if (!providerExtensions)
            {
                var allowed = type switch
                {
                    "text" => true,
                    "refusal" => role == "assistant",
                    "image_url" or "input_audio" or "file" => role == "user",
                    _ => false
                };
                Require(allowed, $"{partPath}.type", "Unsupported content type for this message role. Provider content is available on /v1/conduit/chat/completions.");
            }
            switch (type)
            {
                case "text":
                    RequiredString(part, "text", partPath);
                    break;
                case "refusal":
                    RequiredString(part, "refusal", partPath);
                    break;
                case "image_url":
                    var image = RequiredObject(part, "image_url", partPath);
                    RequiredUri(image, "url", $"{partPath}.image_url");
                    OptionalEnum(image, "detail", $"{partPath}.image_url", "auto", "low", "high");
                    break;
                case "input_audio":
                    var audio = RequiredObject(part, "input_audio", partPath);
                    RequiredString(audio, "data", $"{partPath}.input_audio");
                    RequiredEnum(audio, "format", $"{partPath}.input_audio", providerExtensions
                        ? ["wav", "mp3", "aiff", "aac", "ogg", "flac", "m4a", "pcm16", "pcm24"]
                        : ["wav", "mp3"]);
                    break;
                case "file":
                    var file = RequiredObject(part, "file", partPath);
                    foreach (var field in new[] { "filename", "file_data", "file_id" })
                        if (file.TryGetProperty(field, out _))
                            RequiredString(file, field, $"{partPath}.file");
                    break;
                case "video_url":
                    var video = RequiredObject(part, "video_url", partPath);
                    RequiredUri(video, "url", $"{partPath}.video_url");
                    OptionalEnum(video, "detail", $"{partPath}.video_url", "auto", "low", "high");
                    foreach (var field in new[] { "max_frames", "sample_rate", "start_time", "end_time" })
                        if (video.TryGetProperty(field, out var number))
                            Require(number.ValueKind == JsonValueKind.Number &&
                                (field != "max_frames" || number.TryGetInt32(out _)),
                                $"{partPath}.video_url.{field}", "Expected a numeric processing option.");
                    break;
            }
            if (type is "text" or "image_url" or "input_audio" or "file" &&
                part.TryGetProperty("prompt_cache_breakpoint", out _))
            {
                var breakpoint = RequiredObject(part, "prompt_cache_breakpoint", partPath);
                RequiredEnum(breakpoint, "mode", $"{partPath}.prompt_cache_breakpoint", "explicit");
            }
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string field, string path)
    {
        Require(parent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object,
            $"{path}.{field}", "Expected an object.");
        return value;
    }

    private static string RequiredString(JsonElement parent, string field, string path)
    {
        Require(parent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String,
            $"{path}.{field}", "Expected a string.");
        return value.GetString()!;
    }

    private static void RequiredEnum(JsonElement parent, string field, string path, params string[] values)
    {
        var value = RequiredString(parent, field, path);
        Require(values.Contains(value, StringComparer.Ordinal), $"{path}.{field}", "Unsupported value.");
    }

    private static void RequiredUri(JsonElement parent, string field, string path)
    {
        var value = RequiredString(parent, field, path);
        Require(Uri.TryCreate(value, UriKind.Absolute, out _), $"{path}.{field}", "Expected an absolute URI or data URL.");
    }

    private static void OptionalEnum(JsonElement parent, string field, string path, params string[] values)
    {
        if (parent.TryGetProperty(field, out _))
            RequiredEnum(parent, field, path, values);
    }

    private static void Require(bool condition, string path, string message)
    {
        if (!condition)
            throw new InvalidRequestException(message, "invalid_chat_content", path);
    }
}
