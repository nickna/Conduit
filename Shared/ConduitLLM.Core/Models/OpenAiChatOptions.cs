using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

public sealed class ChatAudioOptions
{
    [JsonPropertyName("voice")]
    public required string Voice { get; set; }

    [JsonPropertyName("format")]
    public required string Format { get; set; }
}

public sealed class ChatPrediction
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "content";

    [JsonPropertyName("content")]
    public required JsonElement Content { get; set; }
}

public sealed class PromptCacheOptions
{
    [JsonPropertyName("retention")]
    public string? Retention { get; set; }
}

public sealed class WebSearchOptions
{
    [JsonPropertyName("search_context_size")]
    public string? SearchContextSize { get; set; }

    [JsonPropertyName("user_location")]
    public WebSearchUserLocation? UserLocation { get; set; }
}

public sealed class WebSearchUserLocation
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "approximate";

    [JsonPropertyName("approximate")]
    public WebSearchApproximateLocation? Approximate { get; set; }
}

public sealed class WebSearchApproximateLocation
{
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }
}

[JsonConverter(typeof(LegacyFunctionChoiceConverter))]
public sealed class LegacyFunctionChoice
{
    private LegacyFunctionChoice(string? mode, string? functionName)
    {
        Mode = mode;
        FunctionName = functionName;
    }

    public string? Mode { get; }
    public string? FunctionName { get; }

    public static LegacyFunctionChoice None => new("none", null);
    public static LegacyFunctionChoice Auto => new("auto", null);
    public static LegacyFunctionChoice Function(string name) => new(null, name);
}

public sealed class LegacyFunctionChoiceConverter : JsonConverter<LegacyFunctionChoice>
{
    public override LegacyFunctionChoice Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() switch
            {
                "none" => LegacyFunctionChoice.None,
                "auto" => LegacyFunctionChoice.Auto,
                var value => throw new JsonException($"Unsupported function_call value '{value}'.")
            };
        }

        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.TryGetProperty("name", out var name) &&
            !string.IsNullOrWhiteSpace(name.GetString()))
        {
            return LegacyFunctionChoice.Function(name.GetString()!);
        }

        throw new JsonException("function_call must be 'none', 'auto', or an object containing name.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        LegacyFunctionChoice value,
        JsonSerializerOptions options)
    {
        if (value.Mode is not null)
        {
            writer.WriteStringValue(value.Mode);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("name", value.FunctionName);
        writer.WriteEndObject();
    }
}

public sealed class StringOrStringArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString() ?? string.Empty];

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a stop string or string array.");

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Stop arrays may contain only strings.");
            values.Add(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("Unterminated stop array.");
        return values;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string> value,
        JsonSerializerOptions options) =>
        JsonSerializer.Serialize(
            writer,
            value,
            new Serialization.CoreHttpJsonContext(
                new JsonSerializerOptions(options)).ListString);
}
