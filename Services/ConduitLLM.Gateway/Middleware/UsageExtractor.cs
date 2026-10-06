using System.Text.Json;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Middleware
{
    /// <summary>
    /// Helpers for classifying requests and serializing typed usage metadata.
    /// </summary>
    public static class UsageExtractor
    {
        /// <summary>
        /// Determines the request type from the API path.
        /// </summary>
        /// <param name="path">The request path</param>
        /// <returns>The type of request (chat, completion, embedding, etc.)</returns>
        public static string DetermineRequestType(PathString path)
        {
            var pathValue = path.Value?.ToLowerInvariant() ?? "";

            if (pathValue.Contains("/chat/completions"))
                return "chat";
            if (pathValue.Contains("/responses"))
                return "responses";
            if (pathValue.Contains("/completions"))
                return "completion";
            if (pathValue.Contains("/embeddings"))
                return "embedding";
            if (pathValue.Contains("/images/generations"))
                return "image";
            if (pathValue.Contains("/audio/transcriptions"))
                return "transcription";
            if (pathValue.Contains("/audio/speech"))
                return "tts";
            if (pathValue.Contains("/videos/generations"))
                return "video";
            if (pathValue.Contains("/rerank"))
                return "rerank";
            if (pathValue.Contains("/functions/execute"))
                return "function";

            return "other";
        }

        /// <summary>
        /// Calculates the response time from the request start time stored in HttpContext.
        /// </summary>
        /// <param name="context">The HTTP context</param>
        /// <returns>Response time in milliseconds</returns>
        public static double GetResponseTime(HttpContext context)
        {
            if (context.Items.TryGetValue("RequestStartTime", out var startTimeObj) &&
                startTimeObj is DateTime startTime)
            {
                return (DateTime.UtcNow - startTime).TotalMilliseconds;
            }

            return 0;
        }

        /// <summary>
        /// Serializes chat tool call data to JSON for storage in metadata.
        /// </summary>
        /// <param name="data">The chat tool call data</param>
        /// <returns>JSON string representation</returns>
        public static string? SerializeChatToolCalls(ChatToolCallData? data)
        {
            if (data == null || data.ToolCalls.Count == 0)
                return null;

            return JsonSerializer.Serialize(
                new ChatToolCallsMetadata(
                    "chat_with_tools",
                    data.ToolCalls.Count,
                    data.ToolCalls.Select(tc => new ChatToolCallMetadata(
                        tc.Id,
                        tc.Type,
                        tc.FunctionName,
                        tc.HasArguments)).ToList()),
                GatewayInternalJsonContext.Default.ChatToolCallsMetadata);
        }
    }

    /// <summary>
    /// Represents tool usage data reported by a provider.
    /// </summary>
    public class ToolUsageData
    {
        /// <summary>
        /// List of tools that were used in the request.
        /// </summary>
        public List<ToolUsageItem> Tools { get; set; } = new();
    }

    /// <summary>
    /// Represents usage information for a specific tool.
    /// </summary>
    public class ToolUsageItem
    {
        /// <summary>
        /// Name of the tool (e.g., "code_interpreter", "browser_search")
        /// </summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>
        /// Number of times the tool was invoked.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Duration of tool usage in the billing unit (hours/minutes).
        /// When set explicitly, takes priority over DurationSeconds.
        /// </summary>
        public decimal? Duration { get; set; }

        /// <summary>
        /// Raw duration in seconds as reported by the provider.
        /// Used by CalculateUsageAmount to convert to the appropriate billing unit.
        /// </summary>
        public decimal? DurationSeconds { get; set; }
    }

    /// <summary>
    /// Represents tool/function calls captured from typed chat accounting evidence.
    /// </summary>
    public class ChatToolCallData
    {
        /// <summary>
        /// List of tool calls made by the LLM in the response.
        /// </summary>
        public List<ChatToolCallItem> ToolCalls { get; set; } = new();
    }

    /// <summary>
    /// Represents a single tool/function call.
    /// </summary>
    public class ChatToolCallItem
    {
        /// <summary>
        /// The unique ID of the tool call (e.g., "call_abc123")
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// The type of tool call (typically "function")
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// The name of the function being called.
        /// </summary>
        public string? FunctionName { get; set; }

        /// <summary>
        /// Whether arguments were provided (actual arguments are not stored for privacy).
        /// </summary>
        public bool HasArguments { get; set; }
    }
}
