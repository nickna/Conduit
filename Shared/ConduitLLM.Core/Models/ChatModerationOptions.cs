using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>Provider moderation configuration for chat input and generated output.</summary>
public sealed class ChatModerationOptions
{
    private string _model = string.Empty;

    /// <summary>The moderation model, such as omni-moderation-latest.</summary>
    [JsonPropertyName("model")]
    public required string Model
    {
        get => _model;
        init => _model = !string.IsNullOrEmpty(value)
            ? value : throw new JsonException("A moderation model is required.");
    }

    /// <summary>Optional input and output moderation policies.</summary>
    [JsonPropertyName("policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatModerationPolicy? Policy { get; init; }
}

/// <summary>Independent moderation rules for request input and generated output.</summary>
public sealed class ChatModerationPolicy
{
    /// <summary>The input moderation rule, when requested.</summary>
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatModerationRule? Input { get; init; }

    /// <summary>The output moderation rule, when requested.</summary>
    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatModerationRule? Output { get; init; }
}

/// <summary>The action a provider should take for moderated content.</summary>
public sealed class ChatModerationRule
{
    /// <summary>Whether to score content or block content rejected by moderation.</summary>
    [JsonPropertyName("mode")]
    public required ChatModerationMode Mode { get; init; }
}

/// <summary>The moderation modes supported by the OpenAI request contract.</summary>
[JsonConverter(typeof(ChatModerationModeConverter))]
public enum ChatModerationMode
{
    /// <summary>Return moderation scores.</summary>
    Score,
    /// <summary>Block content rejected by moderation.</summary>
    Block
}

/// <summary>Restricts moderation modes to their supported JSON string values.</summary>
public sealed class ChatModerationModeConverter : JsonConverter<ChatModerationMode>
{
    /// <inheritdoc />
    public override ChatModerationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "score" => ChatModerationMode.Score,
            "block" => ChatModerationMode.Block,
            _ => throw new JsonException("Moderation mode must be score or block.")
        } : throw new JsonException("Moderation mode must be a string.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ChatModerationMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ChatModerationMode.Score => "score",
            ChatModerationMode.Block => "block",
            _ => throw new JsonException("Moderation mode must be score or block.")
        });
}
