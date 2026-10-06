using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>
/// Represents a tool choice for a chat completion request, specifying which tools the model can use.
/// </summary>
[JsonConverter(typeof(ToolChoiceConverter))]
public class ToolChoice
{
    // Internal storage for the tool choice value
    private readonly object _value;
    private readonly string? _functionName;

    /// <summary>
    /// Constructor for deserialization
    /// </summary>
    public ToolChoice()
    {
        _value = "auto"; // Default to auto
    }

    // Private constructor for creating from an object
    private ToolChoice(object value, string? functionName = null)
    {
        _value = value;
        _functionName = functionName;
    }

    /// <summary>
    /// Serializes the tool choice to the appropriate format for API requests.
    /// </summary>
    /// <returns>The serialized value representing this tool choice.</returns>
    public object GetSerializedValue() => _value;

    /// <summary>
    /// Gets the explicitly selected function name without serializing the object-shaped wire value.
    /// </summary>
    public bool TryGetFunctionName([NotNullWhen(true)] out string? functionName)
    {
        functionName = _functionName;
        return functionName is not null;
    }

    /// <summary>
    /// Indicates the model should not call any functions.
    /// </summary>
    public static ToolChoice None => new("none");

    /// <summary>
    /// Indicates the model can pick between generating a message or calling a function.
    /// </summary>
    public static ToolChoice Auto => new("auto");

    /// <summary>Indicates that the model must call one or more tools.</summary>
    public static ToolChoice Required => new("required");

    /// <summary>
    /// Indicates the model must call the specified function.
    /// </summary>
    /// <param name="functionName">The name of the function that must be called.</param>
    /// <returns>A ToolChoice object specifying the function to call.</returns>
    public static ToolChoice Function(string functionName)
    {
        if (string.IsNullOrEmpty(functionName))
        {
            throw new ArgumentNullException(nameof(functionName), "Function name cannot be null or empty");
        }

        return new(new
        {
            type = "function",
            function = new
            {
                name = functionName
            }
        }, functionName);
    }

    /// <summary>
    /// Creates a custom tool choice with the specified value.
    /// </summary>
    /// <param name="value">The raw value to use for the tool choice.</param>
    /// <returns>A ToolChoice object with the specified value.</returns>
    public static ToolChoice Custom(object value) => new(value);
}

/// <summary>
/// Custom JSON converter for ToolChoice to handle serialization and deserialization.
/// </summary>
public class ToolChoiceConverter : JsonConverter<ToolChoice>
{
    /// <inheritdoc/>
    public override ToolChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (value == "none")
            {
                return ToolChoice.None;
            }
            if (value == "auto" || value == null)
            {
                return ToolChoice.Auto;
            }
            if (value == "required")
            {
                return ToolChoice.Required;
            }
            throw new JsonException($"Unexpected string value for tool_choice: {value}");
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Read the object as JsonDocument
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);

            // Extract function name
            if (doc.RootElement.TryGetProperty("function", out JsonElement functionElement) &&
                functionElement.TryGetProperty("name", out JsonElement nameElement))
            {
                string? functionName = nameElement.GetString();
                if (functionName != null)
                {
                    return ToolChoice.Function(functionName);
                }
            }

            throw new JsonException("Invalid tool_choice object format");
        }

        throw new JsonException($"Unexpected token type for tool_choice: {reader.TokenType}");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ToolChoice value, JsonSerializerOptions options)
    {
        var serializedValue = value.GetSerializedValue();

        if (serializedValue is string stringValue)
        {
            writer.WriteStringValue(stringValue);
        }
        else
        {
            // Serialize the object
            JsonSerializer.Serialize(
                writer,
                serializedValue,
                options.GetTypeInfo(serializedValue.GetType()));
        }
    }
}
