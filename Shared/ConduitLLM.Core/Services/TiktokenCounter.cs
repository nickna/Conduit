using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Token counter implementation using Microsoft.ML.Tokenizers for OpenAI-compatible (tiktoken)
    /// tokenization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TiktokenCounter provides token counting functionality using Microsoft.ML.Tokenizers,
    /// which implements OpenAI's tiktoken algorithm with vocabulary data bundled as NuGet
    /// packages — no runtime download (#1227). This service is essential for:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Accurately estimating token usage for cost calculation</description></item>
    ///   <item><description>Ensuring messages fit within model context windows</description></item>
    ///   <item><description>Determining appropriate chunking strategies for large content</description></item>
    /// </list>
    /// <para>
    /// This implementation provides robust handling for different content types including:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Simple string content</description></item>
    ///   <item><description>Multimodal content with text and images</description></item>
    ///   <item><description>Complex JSON structures</description></item>
    /// </list>
    /// <para>
    /// When the exact encoding for a model cannot be determined, or when tokenization fails,
    /// this implementation falls back to a simple character-based estimation (approximate 4 characters per token).
    /// </para>
    /// </remarks>
    public class TiktokenCounter : ITokenCounter
    {
        // Cache encodings for performance, keyed by the requested tokenizer identifier.
        // A null tokenizer is a cached failure and means "use character-based estimation";
        // the fidelity is cached alongside so it is computed once per tokenizer, not per count.
        private static readonly Dictionary<string, (Tokenizer? Encoding, TokenCountFidelity Fidelity)> _encodings = new();
        private static readonly object _lock = new();
        private readonly ILogger<TiktokenCounter> _logger;
        private readonly IModelCapabilityService? _capabilityService;

        /// <summary>
        /// Initializes a new instance of the <see cref="TiktokenCounter"/> class.
        /// </summary>
        /// <param name="logger">The logger for recording diagnostic information.</param>
        /// <param name="capabilityService">Service for retrieving model capabilities from configuration.</param>
        /// <exception cref="ArgumentNullException">Thrown when logger is null.</exception>
        public TiktokenCounter(ILogger<TiktokenCounter> logger, IModelCapabilityService? capabilityService = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _capabilityService = capabilityService;

            if (capabilityService == null)
            {
                _logger.LogWarning("ModelCapabilityService not available, using fallback tokenizer detection");
            }
        }

        /// <inheritdoc />
        public async Task<TokenCount> EstimateTokenCountAsync(string modelName, List<Message> messages, IReadOnlyList<Tool>? tools = null)
        {
            if (messages == null || !messages.Any())
            {
                return Finish(new TokenCount(0, TokenCountFidelity.Exact));
            }

            try
            {
                var (encoding, fidelity) = await GetEncodingForModelAsync(modelName);
                if (encoding == null)
                {
                    // Fallback strategy if we can't get the right encoding
                    _logger.LogWarning("Could not determine encoding for model {ModelName}. Using fallback token estimation method.", modelName);
                    return Finish(new TokenCount(FallbackEstimateTokens(messages, tools), TokenCountFidelity.CharacterHeuristic));
                }

                int tokenCount = 0;
                foreach (var message in messages)
                {
                    // OpenAI adds tokens per message and per role.
                    // These numbers are based on OpenAI's tokenization approach
                    tokenCount += 4; // Every message follows <|start|>{role/name}\n{content}<|end|>\n


                    if (message.Role != null)
                    {
                        try
                        {
                            tokenCount += encoding.CountTokens(message.Role);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error encoding role. Using fallback estimate.");
                            tokenCount += message.Role.Length / 4;
                            fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.CharacterHeuristic);
                        }
                    }

                    if (message.Content != null)
                    {
                        try
                        {
                            if (message.Content is string contentStr)
                            {
                                // Simple string content
                                tokenCount += encoding.CountTokens(contentStr);
                            }
                            else if (message.Content is JsonElement jsonElement)
                            {
                                // Handle JsonElement (common when deserialized from JSON)
                                tokenCount += EstimateJsonElementTokens(jsonElement, encoding, ref fidelity);
                            }
                            else
                            {
                                // Typed content parts (TextContentPart / ImageUrlContentPart) and
                                // other structured content: serialize and reuse the JSON path so
                                // text and images are counted identically however content arrived.
                                tokenCount += EstimateContentObjectTokens(message.Content, encoding, ref fidelity);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error encoding content. Using fallback estimate.");
                            // Fallback calculation
                            string contentStr = message.Content.ToString() ?? "";
                            tokenCount += contentStr.Length / 4;
                            fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.CharacterHeuristic);
                        }
                    }

                    // Add handling for 'Name' property if present
                    if (!string.IsNullOrEmpty(message.Name))
                    {
                        try
                        {
                            tokenCount += encoding.CountTokens(message.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error encoding name. Using fallback estimate.");
                            tokenCount += message.Name.Length / 4;
                            fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.CharacterHeuristic);
                        }
                        tokenCount += 1; // Additional overhead for name field
                    }

                    // Assistant tool calls travel back to the provider as prompt content on the
                    // next turn, so agentic histories are structurally under-counted without them.
                    if (message.ToolCalls is { Count: > 0 })
                    {
                        try
                        {
                            tokenCount += CountToolCallTokens(message.ToolCalls, encoding);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error encoding tool calls. Using fallback estimate.");
                            tokenCount += ToolCallFallbackChars(message.ToolCalls) / 4;
                            fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.CharacterHeuristic);
                        }
                    }
                }

                if (tools is { Count: > 0 })
                {
                    try
                    {
                        tokenCount += CountToolDefinitionTokens(tools, encoding);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error encoding tool definitions. Using fallback estimate.");
                        tokenCount += ToolDefinitionFallbackChars(tools) / 4;
                        fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.CharacterHeuristic);
                    }
                }

                tokenCount += 3; // Every reply is primed with <|start|>assistant<|message|>

                return Finish(new TokenCount(tokenCount, fidelity));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error estimating token count. Using fallback method.");
                return Finish(new TokenCount(FallbackEstimateTokens(messages, tools), TokenCountFidelity.CharacterHeuristic));
            }
        }

        /// <summary>
        /// Counts the tokens an assistant message's tool calls contribute when replayed as prompt
        /// context: the function name and raw JSON arguments, plus a small per-call framing cost.
        /// </summary>
        /// <remarks>
        /// Providers do not publish their exact serialization of historical tool calls, so the
        /// per-call overhead of 3 mirrors the reply-priming constant used elsewhere in this
        /// counter. The name and arguments dominate in practice.
        /// </remarks>
        private static int CountToolCallTokens(IReadOnlyList<ToolCall> toolCalls, Tokenizer encoding)
        {
            int tokenCount = 0;
            foreach (var toolCall in toolCalls)
            {
                tokenCount += 3;
                tokenCount += encoding.CountTokens(toolCall.Function.Name);
                tokenCount += encoding.CountTokens(toolCall.Function.Arguments);
            }

            return tokenCount;
        }

        /// <summary>
        /// Counts the tokens a request's tool definitions contribute: providers inject each
        /// function's name, description and JSON-schema parameters into the prompt.
        /// </summary>
        /// <remarks>
        /// OpenAI compacts schemas into a TypeScript-like namespace listing before tokenizing;
        /// counting the raw JSON schema instead over-counts slightly (schema punctuation the
        /// compaction strips), which errs on the safe side for reservations and fallback billing.
        /// The constants — 10 for the scaffolding around the tools block, 6 per function — follow
        /// the same published community measurements the compaction format comes from.
        /// </remarks>
        private static int CountToolDefinitionTokens(IReadOnlyList<Tool> tools, Tokenizer encoding)
        {
            int tokenCount = 10;
            foreach (var tool in tools)
            {
                tokenCount += 6;
                tokenCount += encoding.CountTokens(tool.Function.Name);
                if (!string.IsNullOrEmpty(tool.Function.Description))
                {
                    tokenCount += encoding.CountTokens(tool.Function.Description);
                }
                if (tool.Function.Parameters is not null)
                {
                    tokenCount += encoding.CountTokens(tool.Function.Parameters.ToJsonString());
                }
            }

            return tokenCount;
        }

        private static int ToolCallFallbackChars(IReadOnlyList<ToolCall> toolCalls) =>
            toolCalls.Sum(c => c.Function.Name.Length + c.Function.Arguments.Length);

        private static int ToolDefinitionFallbackChars(IReadOnlyList<Tool> tools) =>
            tools.Sum(t => t.Function.Name.Length
                + (t.Function.Description?.Length ?? 0)
                + (t.Function.Parameters?.ToJsonString().Length ?? 0));

        /// <inheritdoc />
        public async Task<TokenCount> EstimateTokenCountAsync(string modelName, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Finish(new TokenCount(0, TokenCountFidelity.Exact));
            }

            try
            {
                var (encoding, fidelity) = await GetEncodingForModelAsync(modelName);
                if (encoding == null)
                {
                    // Fallback strategy
                    _logger.LogWarning("Could not determine encoding for model {ModelName}. Using fallback token estimation method.", modelName);
                    return Finish(new TokenCount(FallbackEstimateTokens(text), TokenCountFidelity.CharacterHeuristic));
                }

                try
                {
                    return Finish(new TokenCount(encoding.CountTokens(text), fidelity));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error encoding text. Using fallback estimate.");
                    return Finish(new TokenCount(FallbackEstimateTokens(text), TokenCountFidelity.CharacterHeuristic));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error estimating token count. Using fallback method.");
                return Finish(new TokenCount(FallbackEstimateTokens(text), TokenCountFidelity.CharacterHeuristic));
            }
        }

        /// <summary>
        /// Records the estimate's fidelity to metrics before handing it back. The
        /// character_heuristic series is the operational alarm for broken vocabulary data (#1227).
        /// </summary>
        private static TokenCount Finish(TokenCount count)
        {
            TokenCountingMetrics.Record(count.Fidelity);
            return count;
        }

        /// <summary>
        /// Gets the appropriate tiktoken tokenizer, and the fidelity its counts will have, for a
        /// given model asynchronously.
        /// </summary>
        /// <param name="modelName">The name of the model to get encoding for.</param>
        /// <returns>The tokenizer (null if it cannot be determined) and the resulting fidelity.</returns>
        private async Task<(Tokenizer? Encoding, TokenCountFidelity Fidelity)> GetEncodingForModelAsync(string modelName)
        {
            try
            {
                string? tokenizerType = null;

                // Try to get tokenizer type from capability service first
                if (_capabilityService != null)
                {
                    try
                    {
                        tokenizerType = await _capabilityService.GetTokenizerTypeAsync(modelName);
                        if (!string.IsNullOrEmpty(tokenizerType))
                        {
                            _logger.LogDebug("Using tokenizer {TokenizerType} from capability service for model {Model}", tokenizerType, modelName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error getting tokenizer type from capability service for model {Model}", modelName);
                    }
                }

                return GetOrCreateEncoding(tokenizerType, modelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetEncodingForModelAsync");
                return (null, TokenCountFidelity.CharacterHeuristic);
            }
        }

        /// <summary>
        /// Gets or creates a tiktoken tokenizer with thread-safe caching.
        /// </summary>
        /// <param name="tokenizerType">
        /// The tokenizer identifier from model metadata (a <c>TokenizerType</c> name such as
        /// <c>Cl100KBase</c> or <c>LLaMA3</c>), or null to use the default encoding.
        /// </param>
        /// <param name="modelName">The model name (for logging purposes).</param>
        /// <returns>The tokenizer (null if it cannot be created) and the resulting fidelity.</returns>
        /// <remarks>
        /// The cache is keyed on <paramref name="tokenizerType"/> rather than on the resolved
        /// encoding name. Keying it on the resolved name meant an unresolvable tokenizer never
        /// populated an entry under the key that was looked up, so every single token count
        /// re-entered the failure path and re-logged (#1051).
        /// </remarks>
        private (Tokenizer? Encoding, TokenCountFidelity Fidelity) GetOrCreateEncoding(string? tokenizerType, string modelName)
        {
            var cacheKey = tokenizerType?.Trim() ?? string.Empty;

            lock (_lock)
            {
                if (_encodings.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                var resolved = TokenizerEncodingMap.Resolve(tokenizerType);

                if (!resolved.IsRecognized)
                {
                    _logger.LogWarning(
                        "Unknown tokenizer {TokenizerType} for model {ModelName}; using {EncodingName} for estimation",
                        tokenizerType, modelName, resolved.EncodingName);
                }
                else if (resolved.IsApproximation)
                {
                    _logger.LogDebug(
                        "Tokenizer {TokenizerType} has no Tiktoken equivalent; approximating model {ModelName} with {EncodingName}",
                        tokenizerType, modelName, resolved.EncodingName);
                }

                Tokenizer? encoding;
                try
                {
                    encoding = TiktokenTokenizer.CreateForEncoding(resolved.EncodingName);
                }
                catch (Exception ex)
                {
                    // The map only ever yields encodings whose vocabulary ships in a referenced
                    // Microsoft.ML.Tokenizers.Data.* package, so this indicates a missing package
                    // reference rather than bad configuration or a network failure.
                    _logger.LogError(ex,
                        "Failed to load encoding {EncodingName} for model {ModelName}; falling back to character-based estimation",
                        resolved.EncodingName, modelName);
                    encoding = null;
                }

                var fidelity = encoding is null
                    ? TokenCountFidelity.CharacterHeuristic
                    : resolved.IsApproximation || !resolved.IsRecognized
                        ? TokenCountFidelity.ApproximateVocabulary
                        : TokenCountFidelity.Exact;

                // Cache the failure too, so a broken encoding does not re-throw on every request.
                _encodings[cacheKey] = (encoding, fidelity);
                return (encoding, fidelity);
            }
        }

        /// <summary>
        /// Estimates tokens for content in JsonElement format (common when deserializing JSON).
        /// </summary>
        /// <param name="element">The JsonElement to estimate token count for.</param>
        /// <param name="encoding">The tokenizer encoding to use.</param>
        /// <param name="fidelity">Degraded in place when a part's cost is itself an estimate.</param>
        /// <returns>The estimated token count.</returns>
        /// <remarks>
        /// <list type="bullet">
        ///   <item><description>String elements: Directly tokenized</description></item>
        ///   <item><description>Arrays: Each element treated as a content part</description></item>
        ///   <item><description>Objects: Treated as a single content part</description></item>
        /// </list>
        /// </remarks>
        private static int EstimateJsonElementTokens(JsonElement element, Tokenizer encoding, ref TokenCountFidelity fidelity)
        {
            int tokenCount = 0;

            if (element.ValueKind == JsonValueKind.String)
            {
                string? stringValue = element.GetString();
                if (stringValue != null)
                {
                    tokenCount += encoding.CountTokens(stringValue);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    tokenCount += EstimateContentPartTokens(item, encoding, ref fidelity);
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                tokenCount += EstimateContentPartTokens(element, encoding, ref fidelity);
            }

            return tokenCount;
        }

        /// <summary>
        /// Estimates tokens for structured (non-string, non-JsonElement) content by serializing
        /// it and reusing the JSON content-part logic, so typed content parts count the same as
        /// their deserialized equivalents.
        /// </summary>
        private static int EstimateContentObjectTokens(object content, Tokenizer encoding, ref TokenCountFidelity fidelity)
        {
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(content, CoreHttpJsonContext.Default.Object));
            return EstimateJsonElementTokens(document.RootElement, encoding, ref fidelity);
        }

        /// <summary>
        /// Estimates tokens for one content part. Text parts are tokenized; image parts are
        /// priced through <see cref="ImageTokenCalculator.EstimateImageTokens"/> — the real
        /// detail/resolution vision formula, replacing the flat 65 that under-counted
        /// high-detail images roughly 10-17x (#1231). Media whose duration/page count cannot
        /// be known locally receives a conservative prompt-token reservation.
        /// </summary>
        /// <remarks>
        /// An image whose geometry cannot be determined locally is charged the conservative
        /// high-detail default and degrades <paramref name="fidelity"/>, so billing consumers
        /// buffer the count instead of trusting it as exact. The formula is OpenAI's;
        /// provider-specific image accounting is out of scope here, but the conservative
        /// default is far closer to every provider's real cost than 65 was.
        /// </remarks>
        private static int EstimateContentPartTokens(JsonElement part, Tokenizer encoding, ref TokenCountFidelity fidelity)
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return 0;
            }

            switch (typeElement.GetString())
            {
                case "text":
                    if (part.TryGetProperty("text", out var textElement) &&
                        textElement.ValueKind == JsonValueKind.String &&
                        textElement.GetString() is { } text)
                    {
                        return encoding.CountTokens(text);
                    }
                    return 0;

                case "image_url":
                    string? url = null;
                    string? detail = null;
                    if (part.TryGetProperty("image_url", out var imageUrlElement) &&
                        imageUrlElement.ValueKind == JsonValueKind.Object)
                    {
                        if (imageUrlElement.TryGetProperty("url", out var urlElement) &&
                            urlElement.ValueKind == JsonValueKind.String)
                        {
                            url = urlElement.GetString();
                        }
                        if (imageUrlElement.TryGetProperty("detail", out var detailElement) &&
                            detailElement.ValueKind == JsonValueKind.String)
                        {
                            detail = detailElement.GetString();
                        }
                    }

                    if (url is null)
                    {
                        // Malformed image part: charge the conservative default rather than zero.
                        fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.ApproximateVocabulary);
                        return ImageTokenCalculator.ConservativeHighDetailTokens;
                    }

                    var (imageTokens, isConservativeDefault) = ImageTokenCalculator.EstimateImageTokens(
                        new ImageUrl { Url = url, Detail = detail });
                    if (isConservativeDefault)
                    {
                        fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.ApproximateVocabulary);
                    }
                    return imageTokens;

                case "input_audio":
                    fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.ApproximateVocabulary);
                    return 8_192;

                case "video_url":
                case "file":
                    fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.ApproximateVocabulary);
                    return 16_384;

                default:
                    // Unknown provider extensions must not be treated as free input.
                    fidelity = TokenCount.Worst(fidelity, TokenCountFidelity.ApproximateVocabulary);
                    return 1_024;
            }
        }

        /// <summary>
        /// Provides a fallback method for estimating tokens when the proper encoder can't be used.
        /// </summary>
        /// <param name="messages">The list of messages to estimate token count for.</param>
        /// <returns>The estimated token count.</returns>
        /// <remarks>
        /// <para>
        /// This method uses a simple character-based approximation when the proper tokenizer
        /// cannot be used. It follows these steps:
        /// </para>
        /// <list type="number">
        ///   <item><description>Counts the total characters across all message parts (content, role, name)</description></item>
        ///   <item><description>Divides by 4 to approximate tokens (based on the heuristic that English text averages ~4 chars per token)</description></item>
        /// </list>
        /// <para>
        /// While not as accurate as proper tokenization, this method provides a reasonable
        /// estimate when the correct encoder is unavailable or fails.
        /// </para>
        /// </remarks>
        private int FallbackEstimateTokens(List<Message> messages, IReadOnlyList<Tool>? tools = null)
        {
            // Very rough estimation based on characters
            int totalCharacters = messages.Sum(m =>
                (m.Content != null ? m.Content.ToString()?.Length ?? 0 : 0) +
                (m.Role?.Length ?? 0) +
                (m.Name?.Length ?? 0) +
                (m.ToolCalls is { Count: > 0 } ? ToolCallFallbackChars(m.ToolCalls) : 0));

            if (tools is { Count: > 0 })
            {
                totalCharacters += ToolDefinitionFallbackChars(tools);
            }

            // Rough estimate: 1 token ≈ 4 characters in English
            return totalCharacters / 4;
        }

        /// <summary>
        /// Provides a fallback method for estimating tokens for a single text string.
        /// </summary>
        /// <param name="text">The text to estimate token count for.</param>
        /// <returns>The estimated token count.</returns>
        /// <remarks>
        /// <para>
        /// This method provides a simple character-based approximation when the proper tokenizer
        /// cannot be used. It divides the character count by 4, which is a reasonable
        /// approximation for English text (average ~4 characters per token).
        /// </para>
        /// <para>
        /// While not as accurate as proper tokenization, this method provides a reasonable
        /// fallback when the correct encoder is unavailable or fails.
        /// </para>
        /// </remarks>
        private int FallbackEstimateTokens(string text)
        {
            // Rough approximation: average 4 characters per token
            return text.Length / 4;
        }
    }
}
