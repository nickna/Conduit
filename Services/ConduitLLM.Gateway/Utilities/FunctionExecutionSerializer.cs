using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Utilities;

/// <summary>
/// Utility class for serializing function execution results for request logging metadata.
/// </summary>
public static class FunctionExecutionSerializer
{
    private static readonly JsonSerializerOptions DefaultOptions = CreateOptions();

    /// <summary>
    /// Serializes function execution results to JSON for request log metadata.
    /// This provides richer data than the basic chat tool calls format,
    /// including execution status, cost, and links to FunctionExecution records.
    /// </summary>
    /// <param name="results">The list of function execution results to serialize.</param>
    /// <returns>JSON string representation of the function execution results.</returns>
    public static string SerializeFunctionExecutionResults(IReadOnlyList<ToolExecutionEvent> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return JsonSerializer.Serialize(
            new FunctionExecutionResultsMetadata(
                "chat_with_functions",
                results.Count,
                results.Sum(r => r.Cost ?? 0m),
                results.Count(r => r.Status == "completed"),
                results.Count(r => r.Status == "failed"),
                results.ToList()),
            GatewayJsonTypeInfo.Require<FunctionExecutionResultsMetadata>(DefaultOptions));
    }

    /// <summary>
    /// Deserializes function execution metadata JSON back to a structured object.
    /// Useful for parsing stored metadata from request logs.
    /// </summary>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <returns>Deserialized function execution metadata or null if parsing fails.</returns>
    public static FunctionExecutionMetadata? DeserializeFunctionExecutionMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(
                json,
                GatewayJsonTypeInfo.Require<FunctionExecutionMetadata>(DefaultOptions));
        }
        catch
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = GatewayInternalJsonContext.Default,
            WriteIndented = false
        };
        options.Converters.Add(new ToolExecutionEventMetadataConverter());
        return options;
    }
}

/// <summary>
/// Represents the metadata structure for function execution results stored in request logs.
/// </summary>
public class FunctionExecutionMetadata
{
    /// <summary>
    /// The type identifier for this metadata (always "chat_with_functions").
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Total number of function calls in this request.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("functionCallCount")]
    public int FunctionCallCount { get; set; }

    /// <summary>
    /// Total cost of all function executions.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    /// <summary>
    /// Number of successful function executions.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed function executions.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("failedCount")]
    public int FailedCount { get; set; }

    /// <summary>
    /// List of individual function call details.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("functionCalls")]
    public List<ToolExecutionEvent>? FunctionCalls { get; set; }
}

/// <summary>
/// Reads legacy camelCase request-log entries and writes the canonical snake_case
/// <see cref="ToolExecutionEvent"/> shape.
/// </summary>
internal sealed class ToolExecutionEventMetadataConverter : JsonConverter<ToolExecutionEvent>
{
    public override ToolExecutionEvent Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a function execution object.");
        }

        var result = new ToolExecutionEvent { Status = string.Empty };
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a function execution property.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of function execution metadata.");
            }

            switch (propertyName)
            {
                case "tool_call_id":
                case "toolCallId":
                    result.ToolCallId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "function_name":
                case "functionName":
                    result.FunctionName = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "status":
                    result.Status = reader.TokenType == JsonTokenType.Null ? string.Empty : reader.GetString() ?? string.Empty;
                    break;
                case "cost":
                    result.Cost = reader.TokenType == JsonTokenType.Null ? null : reader.GetDecimal();
                    break;
                case "error_message":
                case "errorMessage":
                    result.ErrorMessage = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "function_execution_id":
                case "functionExecutionId":
                    result.FunctionExecutionId = reader.TokenType == JsonTokenType.Null ? null : reader.GetGuid();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ToolExecutionEvent value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.ToolCallId is not null)
        {
            writer.WriteString("tool_call_id", value.ToolCallId);
        }
        if (value.FunctionName is not null)
        {
            writer.WriteString("function_name", value.FunctionName);
        }
        writer.WriteString("status", value.Status);
        if (value.Cost.HasValue)
        {
            writer.WriteNumber("cost", value.Cost.Value);
        }
        if (value.ErrorMessage is not null)
        {
            writer.WriteString("error_message", value.ErrorMessage);
        }
        if (value.FunctionExecutionId.HasValue)
        {
            writer.WriteString("function_execution_id", value.FunctionExecutionId.Value);
        }
        writer.WriteEndObject();
    }
}
