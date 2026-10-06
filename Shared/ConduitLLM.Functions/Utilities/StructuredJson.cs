using System.Text.Json;
using ConduitLLM.Functions.Serialization;

namespace ConduitLLM.Functions.Utilities;

/// <summary>
/// Converts JSON object columns at the persistence boundary without exposing serialized JSON
/// strings through API contracts.
/// </summary>
public static class StructuredJson
{
    public static Dictionary<string, JsonElement>? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document.RootElement
                    .EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone());
            }

            return new Dictionary<string, JsonElement>
            {
                ["value"] = document.RootElement.Clone()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? SerializeObject(Dictionary<string, JsonElement>? value) =>
        value is null
            ? null
            : JsonSerializer.Serialize(
                value,
                FunctionsJsonContext.Default.DictionaryStringJsonElement);
}
