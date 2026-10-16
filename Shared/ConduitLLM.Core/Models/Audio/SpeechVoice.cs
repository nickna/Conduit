using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models.Audio;

/// <summary>A stock voice ID string or an object referring to a custom voice.</summary>
[JsonConverter(typeof(SpeechVoiceConverter))]
public sealed class SpeechVoice
{
    private SpeechVoice(string id, bool custom)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("A voice ID is required.", nameof(id));
        Id = id;
        IsCustom = custom;
    }

    /// <summary>The stock or custom voice identifier.</summary>
    public string Id { get; }

    /// <summary>Whether to serialize the voice as a custom voice object.</summary>
    public bool IsCustom { get; }

    /// <summary>Uses the provider's stock voice ID string representation.</summary>
    public static SpeechVoice FromId(string id) => new(id, false);

    /// <summary>Uses the custom voice object representation.</summary>
    public static SpeechVoice Custom(string id) => new(id, true);

    /// <summary>Preserves convenient assignment of stock voice ID strings.</summary>
    public static implicit operator SpeechVoice(string id) => FromId(id);
}

/// <summary>Reads and writes supported speech voice forms without reflection.</summary>
public sealed class SpeechVoiceConverter : JsonConverter<SpeechVoice>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override SpeechVoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var id = reader.GetString();
            if (!string.IsNullOrEmpty(id)) return SpeechVoice.FromId(id);
            throw new JsonException("A voice ID is required.");
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(id.GetString()))
                return SpeechVoice.Custom(id.GetString()!);
            throw new JsonException("A custom voice requires a string id.");
        }
        throw new JsonException("Voice must be a voice ID string or a custom voice object.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SpeechVoice value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("A voice is required.");
        if (value.IsCustom)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteEndObject();
        }
        else writer.WriteStringValue(value.Id);
    }
}
