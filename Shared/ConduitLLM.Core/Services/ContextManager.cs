using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Service to manage context window sizes in LLM requests by trimming messages to fit within token limits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ContextManager provides intelligent message trimming to ensure that LLM requests stay 
    /// within the maximum context window size of the model being used. This prevents token limit
    /// errors that would otherwise cause requests to fail.
    /// </para>
    /// <para>
    /// Key features:
    /// - Preserves system messages for maintaining crucial instructions
    /// - Removes oldest non-system messages first to preserve recent conversation context
    /// - Ensures enough tokens are reserved for completion generation
    /// - Handles multimodal content appropriately
    /// </para>
    /// <para>
    /// The service integrates with the token counting system to accurately estimate token usage
    /// for different model types.
    /// </para>
    /// </remarks>
    public class ContextManager : IContextManager
    {
        private readonly ITokenCounter _tokenCounter;
        private readonly ILogger<ContextManager> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ContextManager"/> class.
        /// </summary>
        /// <param name="tokenCounter">The token counter service used for estimating token usage.</param>
        /// <param name="logger">The logger for recording diagnostic information.</param>
        /// <exception cref="ArgumentNullException">Thrown when tokenCounter or logger is null.</exception>
        public ContextManager(ITokenCounter tokenCounter, ILogger<ContextManager> logger)
        {
            _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Manages the context window size by trimming messages if needed to fit within the token limit.
        /// </summary>
        /// <param name="request">The original chat completion request.</param>
        /// <param name="maxContextTokens">The maximum context window size in tokens.</param>
        /// <returns>
        /// A potentially new request object with trimmed messages that fit within the token limit.
        /// If no trimming is needed, the original request is returned.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method follows these steps to manage the context window:
        /// </para>
        /// <list type="number">
        /// <item>
        ///   <description>Calculates the current token count for all messages</description>
        /// </item>
        /// <item>
        ///   <description>Reserves tokens for the completion based on MaxTokens in the request</description>
        /// </item>
        /// <item>
        ///   <description>If the current token count exceeds the available context tokens, begins trimming</description>
        /// </item>
        /// <item>
        ///   <description>Removes the oldest non-system messages first</description>
        /// </item>
        /// <item>
        ///   <description>Re-calculates token count after each removal</description>
        /// </item>
        /// <item>
        ///   <description>Creates a new request with the trimmed message list if any messages were removed</description>
        /// </item>
        /// </list>
        /// <para>
        /// System messages are preserved to maintain crucial instructions, and the method ensures
        /// a minimum context size (100 tokens) is always available regardless of the completion token
        /// reservation.
        /// </para>
        /// </remarks>
        public async Task<ChatCompletionRequest> ManageContextAsync(ChatCompletionRequest request, int? maxContextTokens)
        {
            // Validate inputs
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Early exit conditions
            if (maxContextTokens == null || maxContextTokens <= 0 || request.Messages == null || !request.Messages.Any())
            {
                return request; // Nothing to do if no limit or no messages
            }

            // Get the current token count using the token counter. Context trimming compares
            // against a hard window, so the count alone is enough; fidelity is not consulted.
            int currentTokens = (await _tokenCounter.EstimateTokenCountAsync(request.Model, request.Messages)).Tokens;

            // Account for completion tokens if MaxTokens is specified in the request
            int reservedCompletionTokens = request.MaxTokens ?? 0;

            // Calculate available tokens for the context window
            int availableContextTokens = maxContextTokens.Value - reservedCompletionTokens;

            // Ensure a minimum context size (don't trim to nothing)
            if (availableContextTokens < 100)
            {
                _logger.LogWarning("Available context tokens ({AvailableTokens}) is very low due to high MaxTokens ({MaxTokens}) setting. Using minimum context size.",
                    availableContextTokens, request.MaxTokens);
                availableContextTokens = 100; // Minimum reasonable context window
            }

            // Check if we need to trim messages
            if (currentTokens <= availableContextTokens)
            {
                return request; // Already within limits
            }

            _logger.LogInformation(
                "Context trimming activated. Current tokens: {CurrentTokens}, Max available context tokens: {MaxTokens}",
                currentTokens, availableContextTokens);

            // Create a copy of the messages for trimming
            var trimmedMessages = new List<Message>(request.Messages);

            // Track system messages for special handling
            var systemMessages = trimmedMessages
                .Where(m => m.Role == MessageRole.System)
                .ToList();

            // Keep trimming until under the limit or only system messages remain
            while (currentTokens > availableContextTokens &&
                   trimmedMessages.Count() > systemMessages.Count())
            {
                // Find the oldest non-system message (typically user or assistant message)
                int indexToRemove = -1;
                for (int i = 0; i < trimmedMessages.Count(); i++)
                {
                    if (trimmedMessages[i].Role != MessageRole.System)
                    {
                        indexToRemove = i;
                        break;
                    }
                }

                // Check if we have non-system messages to remove
                if (indexToRemove == -1)
                {
                    // Only system messages left, but still over limit
                    _logger.LogWarning(
                        "Context trimming couldn't reduce token count below limit. " +
                        "Only system messages remain but token count {CurrentTokens} exceeds limit {MaxTokens}.",
                        currentTokens, availableContextTokens);

                    // We could potentially trim system messages too, but for now let's preserve them
                    break;
                }

                // Log which message is being removed
                var messageToRemove = trimmedMessages[indexToRemove];
                _logger.LogDebug("Trimming message with role {Role}, content starts with: {ContentPreview}",
                    messageToRemove.Role,
                    GetContentPreview(messageToRemove.Content, 20));

                // Remove the message
                trimmedMessages.RemoveAt(indexToRemove);

                // Re-estimate token count after removal
                currentTokens = (await _tokenCounter.EstimateTokenCountAsync(request.Model, trimmedMessages)).Tokens;
            }

            // If we removed messages, create a new request with trimmed messages
            if (trimmedMessages.Count() < request.Messages.Count())
            {
                _logger.LogInformation(
                    "Trimmed {RemovedCount} messages to fit context window. Original: {OriginalCount}, New: {NewCount}, Tokens: {TokenCount}",
                    request.Messages.Count() - trimmedMessages.Count(),
                    request.Messages.Count(),
                    trimmedMessages.Count(),
                    currentTokens);

                // Create a new request with the trimmed messages
                // Clone the request to avoid modifying the original
                var newRequest = new ChatCompletionRequest
                {
                    Model = request.Model,
                    Messages = trimmedMessages,
                    Temperature = request.Temperature,
                    MaxTokens = request.MaxTokens,
                    MaxCompletionTokens = request.MaxCompletionTokens,
                    TopP = request.TopP,
                    TopK = request.TopK,
                    N = request.N,
                    Stream = request.Stream,
                    StreamOptions = request.StreamOptions,
                    Stop = request.Stop,
                    User = request.User,
                    SessionId = request.SessionId,
                    Tools = request.Tools,
                    ToolChoice = request.ToolChoice,
                    Functions = request.Functions,
                    FunctionCall = request.FunctionCall,
                    ResponseFormat = request.ResponseFormat,
                    Reasoning = request.Reasoning,
                    ReasoningEffort = request.ReasoningEffort,
                    ParallelToolCalls = request.ParallelToolCalls,
                    Modalities = request.Modalities,
                    Audio = request.Audio,
                    Prediction = request.Prediction,
                    Logprobs = request.Logprobs,
                    TopLogprobs = request.TopLogprobs,
                    ServiceTier = request.ServiceTier,
                    Store = request.Store,
                    Metadata = request.Metadata,
                    SafetyIdentifier = request.SafetyIdentifier,
                    Verbosity = request.Verbosity,
                    Moderation = request.Moderation,
                    PromptCacheKey = request.PromptCacheKey,
                    PromptCacheOptions = request.PromptCacheOptions,
                    PromptCacheRetention = request.PromptCacheRetention,
                    WebSearchOptions = request.WebSearchOptions,
                    Seed = request.Seed,
                    PresencePenalty = request.PresencePenalty,
                    FrequencyPenalty = request.FrequencyPenalty,
                    LogitBias = request.LogitBias,
                    FunctionConfigurationIds = request.FunctionConfigurationIds,
                    EnableAgenticMode = request.EnableAgenticMode,
                    MaxAgenticIterations = request.MaxAgenticIterations,
                    ExtensionData = request.ExtensionData
                };

                return newRequest;
            }

            // If no trimming was done, return the original request
            return request;
        }

        /// <summary>
        /// Gets a preview of message content, handling both string and multimodal content.
        /// </summary>
        /// <param name="content">The message content object, which can be a string, JSON, or other content type.</param>
        /// <param name="maxLength">The maximum length of the preview text.</param>
        /// <returns>A string representation of the content, truncated if necessary.</returns>
        /// <remarks>
        /// <para>
        /// This method handles various content types that can appear in messages:
        /// </para>
        /// <list type="bullet">
        /// <item><description>Simple string content</description></item>
        /// <item><description>JsonElement objects (common when deserialized from JSON)</description></item>
        /// <item><description>Multimodal content (arrays of content blocks)</description></item>
        /// <item><description>Other object types that can be serialized to JSON</description></item>
        /// </list>
        /// <para>
        /// The preview is truncated to the specified maxLength and ellipsis ("...") is added
        /// to indicate truncation.
        /// </para>
        /// </remarks>
        private string GetContentPreview(object? content, int maxLength)
        {
            if (content == null)
                return "[null]";

            if (content is string textContent)
            {
                // Simple string content
                return textContent.Length <= maxLength
                    ? textContent
                    : textContent.Substring(0, maxLength) + "...";
            }

            if (content is JsonElement jsonElement)
            {
                // Handle JsonElement (common when deserialized from JSON)
                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    string? stringValue = jsonElement.GetString();
                    if (!string.IsNullOrEmpty(stringValue) && stringValue.Length > maxLength)
                        return stringValue.Substring(0, maxLength) + "...";
                    return stringValue ?? "[empty]";
                }
                else if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    return "[Multimodal content with " + jsonElement.GetArrayLength() + " parts]";
                }
                else
                {
                    return jsonElement.ToString() ?? "[JsonElement]";
                }
            }

            try
            {
                // Try to provide a sensible preview for other object types
                var typeInfo = CoreJsonTypeInfo.TryGet(content.GetType(), ConduitJsonOptions.Compact);
                if (typeInfo is null)
                    return content.ToString() ?? "[Object]";
                string json = JsonSerializer.Serialize(content, typeInfo);
                if (json.Length > maxLength)
                    return json.Substring(0, maxLength) + "...";
                return json;
            }
            catch
            {
                // Fallback
                return content.ToString() ?? "[object]";
            }
        }
    }
}
