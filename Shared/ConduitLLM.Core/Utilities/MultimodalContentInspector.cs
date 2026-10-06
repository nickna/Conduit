using System.Text.Json;

using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Utilities;

/// <summary>Counts content modalities and transport kinds without retaining media or URL values.</summary>
public sealed record MultimodalContentSummary(
    int TextParts,
    int ImageParts,
    int FileParts,
    int AudioParts,
    int VideoParts,
    int RemoteParts,
    int InlineParts,
    int ProviderFileIdParts);

public static class MultimodalContentInspector
{
    public static MultimodalContentSummary Inspect(IEnumerable<Message> messages)
    {
        var text = 0;
        var image = 0;
        var file = 0;
        var audio = 0;
        var video = 0;
        var remote = 0;
        var inline = 0;
        var fileId = 0;

        foreach (var message in messages)
        {
            if (message.Content is null or string)
                continue;

            JsonElement content;
            try
            {
                content = message.Content is JsonElement element
                    ? element
                    : JsonSerializer.SerializeToElement(
                        message.Content,
                        Serialization.CoreJsonTypeInfo.Require(
                            message.Content.GetType(),
                            Serialization.ConduitJsonOptions.Wire));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                continue;
            }

            if (content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var type))
                    continue;

                switch (type.GetString())
                {
                    case "text":
                        text++;
                        break;
                    case "image_url":
                        image++;
                        CountUrl(part, "image_url", ref remote, ref inline);
                        break;
                    case "video_url":
                        video++;
                        CountUrl(part, "video_url", ref remote, ref inline);
                        break;
                    case "input_audio":
                        audio++;
                        inline++;
                        break;
                    case "file":
                        file++;
                        if (part.TryGetProperty("file", out var fileValue))
                        {
                            if (fileValue.TryGetProperty("file_id", out _))
                                fileId++;
                            else if (fileValue.TryGetProperty("file_data", out var data) &&
                                     data.ValueKind == JsonValueKind.String)
                                CountTransport(data.GetString(), ref remote, ref inline);
                        }
                        break;
                }
            }
        }

        return new(text, image, file, audio, video, remote, inline, fileId);
    }

    private static void CountUrl(JsonElement part, string property, ref int remote, ref int inline)
    {
        if (part.TryGetProperty(property, out var value) &&
            value.TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String)
        {
            CountTransport(url.GetString(), ref remote, ref inline);
        }
    }

    private static void CountTransport(string? value, ref int remote, ref int inline)
    {
        if (DataUrl.TryParse(value, out _))
            inline++;
        else if (value?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
            remote++;
    }
}
