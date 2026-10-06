using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Providers.Helpers;

internal static class PromptCacheMarkerInjector
{
    public static IReadOnlyList<int> ResolveTargets(
        IReadOnlyList<Message> messages,
        IReadOnlyList<CacheInjectionPoint> points)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();

        foreach (var point in points)
        {
            var candidates = Enumerable.Range(0, messages.Count)
                .Where(index =>
                    point.Role is null ||
                    messages[index].Role.Equals(point.Role, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (point.Index is int requested)
            {
                var index = requested < 0 ? candidates.Count + requested : requested;
                if (index >= 0 && index < candidates.Count && seen.Add(candidates[index]))
                {
                    result.Add(candidates[index]);
                }
            }
            else
            {
                foreach (var candidate in candidates)
                {
                    if (seen.Add(candidate))
                    {
                        result.Add(candidate);
                    }
                }
            }
        }

        return result;
    }

    public static bool TryAddMarker(
        object? content,
        string markerKey,
        object markerValue,
        out object? updated)
    {
        updated = content;
        if (content is null)
        {
            return false;
        }

        JsonArray blocks;

        if (content is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        else
        {
            try
            {
                blocks = ToJsonNode(content) as JsonArray ?? new JsonArray();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return false;
            }
        }

        var last = blocks.LastOrDefault() as JsonObject;
        if (last is null || last.ContainsKey(markerKey))
        {
            return false;
        }

        if (last["type"]?.GetValue<string>() == "text" &&
            string.IsNullOrWhiteSpace(last["text"]?.GetValue<string>()))
        {
            return false;
        }

        last[markerKey] = ToJsonNode(markerValue);
        updated = blocks;
        return true;
    }

    public static int CountMarkers(object? content, string markerKey)
    {
        if (content is null or string)
        {
            return 0;
        }

        try
        {
            return (ToJsonNode(content) as JsonArray)?
                .Count(node => node is JsonObject block && block.ContainsKey(markerKey)) ?? 0;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return 0;
        }
    }

    private static JsonNode? ToJsonNode(object value) => value switch
    {
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(
            value,
            value.GetType(),
            CoreHttpJsonContext.Default)
    };
}
