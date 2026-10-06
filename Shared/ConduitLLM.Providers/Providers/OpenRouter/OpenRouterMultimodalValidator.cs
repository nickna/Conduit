using System.Buffers.Text;
using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;

namespace ConduitLLM.Providers.OpenRouter;

/// <summary>Validates OpenRouter chat content without dereferencing caller-provided URLs.</summary>
internal static class OpenRouterMultimodalValidator
{
    internal const long MaximumDecodedPartBytes = 20L * 1024 * 1024;
    internal const long MaximumDecodedRequestBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    private static readonly HashSet<string> VideoMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/mpeg", "video/mov", "video/quicktime", "video/webm"
    };

    private static readonly HashSet<string> AudioFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "mp3", "aiff", "aac", "ogg", "flac", "m4a", "pcm16", "pcm24"
    };

    public static void Validate(ChatCompletionRequest request)
    {
        long totalDecodedBytes = 0;
        for (var messageIndex = 0; messageIndex < request.Messages.Count; messageIndex++)
        {
            var content = request.Messages[messageIndex].Content;
            if (content is null or string)
                continue;

            var root = SerializeToElement(content, $"messages[{messageIndex}].content");
            if (root.ValueKind == JsonValueKind.String)
                continue;
            if (root.ValueKind != JsonValueKind.Array)
                throw Invalid($"messages[{messageIndex}].content must be a string or an array of content parts.");

            var partIndex = 0;
            foreach (var part in root.EnumerateArray())
            {
                var path = $"messages[{messageIndex}].content[{partIndex++}]";
                ValidatePart(part, path, ref totalDecodedBytes);
            }
        }
    }

    private static void ValidatePart(JsonElement part, string path, ref long totalDecodedBytes)
    {
        if (part.ValueKind != JsonValueKind.Object)
            throw Invalid($"{path} must be an object.");
        if (!part.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw Invalid($"{path}.type is required.");

        switch (typeElement.GetString())
        {
            case "text":
                RequireString(part, "text", path);
                break;
            case "image_url":
                ValidateUrlPart(part, "image_url", path, ImageMimeTypes, ref totalDecodedBytes);
                break;
            case "video_url":
                ValidateUrlPart(part, "video_url", path, VideoMimeTypes, ref totalDecodedBytes);
                break;
            case "input_audio":
                ValidateAudio(part, path, ref totalDecodedBytes);
                break;
            case "file":
                ValidateFile(part, path, ref totalDecodedBytes);
                break;
            default:
                throw Invalid($"Unsupported content part type '{typeElement.GetString()}' at {path}.");
        }
    }

    private static void ValidateUrlPart(
        JsonElement part,
        string propertyName,
        string path,
        IReadOnlySet<string> allowedDataMimeTypes,
        ref long totalDecodedBytes)
    {
        var value = RequireObject(part, propertyName, path);
        var url = RequireString(value, "url", $"{path}.{propertyName}");
        ValidateHttpsOrDataUrl(url, $"{path}.{propertyName}.url", allowedDataMimeTypes, ref totalDecodedBytes);
    }

    private static void ValidateFile(JsonElement part, string path, ref long totalDecodedBytes)
    {
        var file = RequireObject(part, "file", path);
        var hasData = file.TryGetProperty("file_data", out var data) &&
                      data.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(data.GetString());
        var hasId = file.TryGetProperty("file_id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString());

        if (hasData == hasId)
            throw Invalid($"{path}.file must contain exactly one of file_data or file_id.");

        if (hasData)
        {
            ValidateHttpsOrDataUrl(
                data.GetString()!,
                $"{path}.file.file_data",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf" },
                ref totalDecodedBytes);
        }
    }

    private static void ValidateAudio(JsonElement part, string path, ref long totalDecodedBytes)
    {
        var audio = RequireObject(part, "input_audio", path);
        var data = RequireString(audio, "data", $"{path}.input_audio");
        var format = RequireString(audio, "format", $"{path}.input_audio");
        if (!AudioFormats.Contains(format))
            throw Invalid($"Unsupported audio format '{format}' at {path}.input_audio.format.");
        if (DataUrl.IsDataUrl(data))
            throw Invalid($"{path}.input_audio.data must contain raw base64, not a data URL.");

        AddBase64Size(data, $"{path}.input_audio.data", ref totalDecodedBytes);
    }

    private static void ValidateHttpsOrDataUrl(
        string value,
        string path,
        IReadOnlySet<string> allowedDataMimeTypes,
        ref long totalDecodedBytes)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!DataUrl.IsDataUrl(value))
            throw Invalid($"{path} must use HTTPS or a supported base64 data URL.");

        if (!DataUrl.TryParse(value, out var dataUrl))
            throw Invalid($"{path} is not a valid data URL.");

        if (!allowedDataMimeTypes.Contains(dataUrl.MediaType) || !dataUrl.IsBase64)
        {
            throw Invalid($"{path} has an unsupported MIME type or encoding.");
        }

        AddBase64Size(dataUrl.Data, path, ref totalDecodedBytes);
    }

    private static void AddBase64Size(string data, string path, ref long totalDecodedBytes)
    {
        if (!Base64.IsValid(data))
            throw Invalid($"{path} is not valid base64.");

        var padding = data.EndsWith("==", StringComparison.Ordinal) ? 2 :
            data.EndsWith('=') ? 1 : 0;
        var decodedBytes = checked((long)data.Length / 4 * 3 - padding);
        if (decodedBytes > MaximumDecodedPartBytes)
            throw Invalid($"{path} exceeds the {MaximumDecodedPartBytes / 1024 / 1024} MiB decoded-size limit.");

        totalDecodedBytes = checked(totalDecodedBytes + decodedBytes);
        if (totalDecodedBytes > MaximumDecodedRequestBytes)
            throw Invalid($"Multimodal content exceeds the {MaximumDecodedRequestBytes / 1024 / 1024} MiB decoded request limit.");
    }

    private static JsonElement RequireObject(JsonElement parent, string propertyName, string path)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Invalid($"{path}.{propertyName} is required and must be an object.");
        return value;
    }

    private static string RequireString(JsonElement parent, string propertyName, string path)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{path}.{propertyName} is required and must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static JsonElement SerializeToElement(object content, string path)
    {
        try
        {
            if (content is JsonElement element)
                return element;

            var options = new JsonSerializerOptions(
                ConduitLLM.Core.Serialization.ConduitJsonOptions.Wire);
            var typeInfo = new ConduitLLM.Core.Serialization.CoreHttpJsonContext(options)
                .GetTypeInfo(content.GetType())
                ?? throw new NotSupportedException(
                    $"Structured content contract '{content.GetType()}' is not registered.");
            return JsonSerializer.SerializeToElement(content, typeInfo);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw Invalid($"{path} could not be serialized as structured content.", exception);
        }
    }

    private static ValidationException Invalid(string message, Exception? inner = null) =>
        inner is null ? new ValidationException(message) : new ValidationException(message, inner);
}
