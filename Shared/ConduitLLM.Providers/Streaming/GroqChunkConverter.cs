using System.Text.Json;
using ConduitLLM.Core.Serialization;

using ConduitLLM.Core.Models;

namespace ConduitLLM.Providers.Streaming;

/// <summary>
/// Transforms Groq-specific streaming usage data into the OpenAI-compatible shape.
/// </summary>
public static class GroqChunkConverter
{
    /// <summary>
    /// Maps a raw Groq streaming chunk while retaining hosted-tool usage as
    /// server-only accounting metadata.
    /// </summary>
    public static ChatCompletionChunk? Convert(JsonElement providerChunk, JsonSerializerOptions options)
    {
        var transformedJson = ExtractGroqUsageJson(providerChunk);
        var context = new CoreHttpJsonContext(new JsonSerializerOptions(options));
        var chunk = JsonSerializer.Deserialize(transformedJson, context.ChatCompletionChunk);
        if (chunk != null)
        {
            chunk.ProviderToolUsage = ExtractHostedToolUsage(providerChunk);
        }

        return chunk;
    }

    /// <summary>
    /// Transforms a Groq chunk to expose <c>x_groq.usage</c> as the standard usage field.
    /// </summary>
    /// <param name="chunk">The original Groq chunk.</param>
    /// <returns>JSON with usage data in the standard location.</returns>
    public static string ExtractGroqUsageJson(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("x_groq", out var xGroq) ||
            !xGroq.TryGetProperty("usage", out var xGroqUsage))
        {
            return chunk.GetRawText();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var property in chunk.EnumerateObject())
            {
                if (property.Name != "x_groq")
                {
                    property.WriteTo(writer);
                }
            }

            writer.WritePropertyName("usage");
            writer.WriteStartObject();

            if (xGroqUsage.TryGetProperty("prompt_tokens", out var promptTokens))
            {
                writer.WriteNumber("prompt_tokens", promptTokens.GetInt32());
            }

            if (xGroqUsage.TryGetProperty("completion_tokens", out var completionTokens))
            {
                writer.WriteNumber("completion_tokens", completionTokens.GetInt32());
            }

            if (xGroqUsage.TryGetProperty("total_tokens", out var totalTokens))
            {
                writer.WriteNumber("total_tokens", totalTokens.GetInt32());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Checks whether a chunk contains Groq-specific usage data.
    /// </summary>
    public static bool HasGroqUsage(JsonElement chunk)
    {
        return chunk.TryGetProperty("x_groq", out var xGroq) &&
               xGroq.TryGetProperty("usage", out _);
    }

    private static ProviderToolUsage? ExtractHostedToolUsage(JsonElement providerChunk)
    {
        if (!providerChunk.TryGetProperty("x_groq", out var xGroq) ||
            !xGroq.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tools = new List<ProviderToolUsageItem>();
        foreach (var toolName in new[] { "code_interpreter", "browser_search", "python" })
        {
            if (!usage.TryGetProperty(toolName, out var countElement) ||
                !countElement.TryGetInt32(out var count) || count <= 0)
            {
                continue;
            }

            decimal? durationSeconds = null;
            if (usage.TryGetProperty($"{toolName}_duration_seconds", out var durationElement) &&
                durationElement.TryGetDecimal(out var duration))
            {
                durationSeconds = duration;
            }

            tools.Add(new ProviderToolUsageItem
            {
                ToolName = toolName == "python" ? "code_interpreter" : toolName,
                Count = count,
                DurationSeconds = durationSeconds
            });
        }

        return tools.Count == 0 ? null : new ProviderToolUsage { Tools = tools };
    }
}
