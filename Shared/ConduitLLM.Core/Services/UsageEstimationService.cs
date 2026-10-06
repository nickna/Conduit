using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Service that estimates usage when providers don't return usage data in streaming responses.
    /// Uses conservative estimation to prevent undercharging customers.
    /// </summary>
    public class UsageEstimationService : IUsageEstimationService
    {
        private readonly ITokenCounter _tokenCounter;
        private readonly IImageTokenCalculator _imageTokenCalculator;
        private readonly ILogger<UsageEstimationService> _logger;
        
        /// <summary>
        /// Buffer percentage added to estimated tokens to ensure we don't undercharge, sized by
        /// how the count was produced (#1233).
        /// </summary>
        /// <remarks>
        /// An exact count keeps the historical 10% — the per-message overhead arithmetic is still
        /// an estimate. A stand-in vocabulary (Claude counted with cl100k_base, ...) is typically
        /// 10-30% off, and the chars/4 heuristic under-counts English prose by 20-40% and CJK by
        /// far more, so those tiers carry proportionally larger buffers.
        /// </remarks>
        private static double BufferFor(TokenCountFidelity fidelity) => fidelity switch
        {
            TokenCountFidelity.Exact => 0.10,
            TokenCountFidelity.ApproximateVocabulary => 0.20,
            _ => 0.40,
        };

        /// <summary>Buffer applied on the character-based fallback paths below.</summary>
        private const double CharacterHeuristicBuffer = 0.40;

        public UsageEstimationService(
            ITokenCounter tokenCounter,
            IImageTokenCalculator imageTokenCalculator,
            ILogger<UsageEstimationService> logger)
        {
            _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
            _imageTokenCalculator = imageTokenCalculator ?? throw new ArgumentNullException(nameof(imageTokenCalculator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public async Task<Usage> EstimateUsageFromStreamingResponseAsync(
            string modelId,
            List<Message> inputMessages,
            string streamedContent,
            IReadOnlyList<Tool>? tools = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentNullException(nameof(modelId));
            if (inputMessages == null || inputMessages.Count == 0)
                throw new ArgumentException("Input messages cannot be null or empty", nameof(inputMessages));
            if (string.IsNullOrEmpty(streamedContent))
                throw new ArgumentException("Streamed content cannot be null or empty", nameof(streamedContent));

            try
            {
                _logger.LogInformation("Estimating usage for model {Model} with {MessageCount} input messages and {OutputLength} characters of output",
                    modelId, inputMessages.Count, streamedContent.Length);

                // Estimate prompt tokens from input messages, including tool definitions
                var promptTokens = await _tokenCounter.EstimateTokenCountAsync(modelId, inputMessages, tools);

                // Estimate completion tokens from streamed content
                var completionTokens = await _tokenCounter.EstimateTokenCountAsync(modelId, streamedContent);

                // Apply a fidelity-sized buffer to avoid undercharging
                var bufferedPromptTokens = (int)Math.Ceiling(promptTokens.Tokens * (1 + BufferFor(promptTokens.Fidelity)));
                var bufferedCompletionTokens = (int)Math.Ceiling(completionTokens.Tokens * (1 + BufferFor(completionTokens.Fidelity)));

                var usage = new Usage
                {
                    PromptTokens = bufferedPromptTokens,
                    CompletionTokens = bufferedCompletionTokens,
                    TotalTokens = bufferedPromptTokens + bufferedCompletionTokens
                };

                _logger.LogInformation(
                    "Estimated usage for model {Model}: Prompt={PromptTokens} (raw={RawPrompt}, {PromptFidelity}), " +
                    "Completion={CompletionTokens} (raw={RawCompletion}, {CompletionFidelity}), Total={TotalTokens}",
                    modelId, usage.PromptTokens, promptTokens.Tokens, promptTokens.Fidelity,
                    usage.CompletionTokens, completionTokens.Tokens, completionTokens.Fidelity,
                    usage.TotalTokens);

                return usage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to estimate usage for model {Model}, falling back to character-based estimation", modelId);
                
                // Fallback to character-based estimation if tokenization fails
                // Using conservative 4 characters per token estimate
                var fallbackPromptTokens = EstimateTokensFromCharacters(await GetTotalCharacterCountAsync(inputMessages));
                var fallbackCompletionTokens = EstimateTokensFromCharacters(streamedContent.Length);

                // This path is character-heuristic by construction, so it gets that tier's buffer
                var bufferedPromptTokens = (int)Math.Ceiling(fallbackPromptTokens * (1 + CharacterHeuristicBuffer));
                var bufferedCompletionTokens = (int)Math.Ceiling(fallbackCompletionTokens * (1 + CharacterHeuristicBuffer));
                
                var fallbackUsage = new Usage
                {
                    PromptTokens = bufferedPromptTokens,
                    CompletionTokens = bufferedCompletionTokens,
                    TotalTokens = bufferedPromptTokens + bufferedCompletionTokens
                };

                _logger.LogWarning(
                    "Using character-based fallback estimation for model {Model}: " +
                    "Prompt={PromptTokens}, Completion={CompletionTokens}, Total={TotalTokens}",
                    modelId, fallbackUsage.PromptTokens, fallbackUsage.CompletionTokens, fallbackUsage.TotalTokens);

                return fallbackUsage;
            }
        }

        /// <inheritdoc/>
        public async Task<Usage> EstimateUsageFromTextAsync(
            string modelId,
            string inputText,
            string outputText,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentNullException(nameof(modelId));
            if (string.IsNullOrEmpty(inputText))
                throw new ArgumentException("Input text cannot be null or empty", nameof(inputText));
            if (string.IsNullOrEmpty(outputText))
                throw new ArgumentException("Output text cannot be null or empty", nameof(outputText));

            try
            {
                _logger.LogInformation("Estimating usage for model {Model} from text: input={InputLength} chars, output={OutputLength} chars",
                    modelId, inputText.Length, outputText.Length);

                // Estimate tokens for input and output
                var promptTokens = await _tokenCounter.EstimateTokenCountAsync(modelId, inputText);
                var completionTokens = await _tokenCounter.EstimateTokenCountAsync(modelId, outputText);

                // Apply a fidelity-sized buffer
                var bufferedPromptTokens = (int)Math.Ceiling(promptTokens.Tokens * (1 + BufferFor(promptTokens.Fidelity)));
                var bufferedCompletionTokens = (int)Math.Ceiling(completionTokens.Tokens * (1 + BufferFor(completionTokens.Fidelity)));
                
                var usage = new Usage
                {
                    PromptTokens = bufferedPromptTokens,
                    CompletionTokens = bufferedCompletionTokens,
                    TotalTokens = bufferedPromptTokens + bufferedCompletionTokens
                };

                _logger.LogInformation(
                    "Estimated text usage for model {Model}: Prompt={PromptTokens}, Completion={CompletionTokens}, Total={TotalTokens}",
                    modelId, usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);

                return usage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to estimate text usage for model {Model}, using fallback", modelId);
                
                // Fallback to character-based estimation
                var fallbackPromptTokens = EstimateTokensFromCharacters(inputText.Length);
                var fallbackCompletionTokens = EstimateTokensFromCharacters(outputText.Length);

                // This path is character-heuristic by construction, so it gets that tier's buffer
                var bufferedPromptTokens = (int)Math.Ceiling(fallbackPromptTokens * (1 + CharacterHeuristicBuffer));
                var bufferedCompletionTokens = (int)Math.Ceiling(fallbackCompletionTokens * (1 + CharacterHeuristicBuffer));
                
                return new Usage
                {
                    PromptTokens = bufferedPromptTokens,
                    CompletionTokens = bufferedCompletionTokens,
                    TotalTokens = bufferedPromptTokens + bufferedCompletionTokens
                };
            }
        }

        /// <summary>
        /// Estimates token count from character count using conservative 4 characters per token ratio.
        /// This is a fallback when proper tokenization fails.
        /// </summary>
        private int EstimateTokensFromCharacters(int characterCount)
        {
            // Conservative estimate: 4 characters per token (OpenAI average is ~4 chars/token for English)
            // This tends to overestimate slightly, which is good for revenue protection
            const double CharsPerToken = 4.0;
            return Math.Max(1, (int)Math.Ceiling(characterCount / CharsPerToken));
        }

        /// <summary>
        /// Gets the total character count from a list of messages, including proper image token calculation.
        /// </summary>
        private async Task<int> GetTotalCharacterCountAsync(List<Message> messages)
        {
            int totalChars = 0;
            foreach (var message in messages)
            {
                // Count role characters
                if (!string.IsNullOrEmpty(message.Role))
                    totalChars += message.Role.Length;
                
                // Count content based on type
                if (message.Content is string stringContent)
                {
                    totalChars += stringContent.Length;
                }
                else if (message.Content is System.Collections.IEnumerable contentParts)
                {
                    // Handle multimodal content (list of content parts)
                    foreach (var part in contentParts)
                    {
                        // Check if it's a known content part type
                        if (part is TextContentPart textPart)
                        {
                            if (!string.IsNullOrEmpty(textPart.Text))
                            {
                                totalChars += textPart.Text.Length;
                            }
                        }
                        else if (part is ImageUrlContentPart imagePart)
                        {
                            try
                            {
                                // Calculate actual image tokens instead of using fixed estimate
                                var imageTokens = await _imageTokenCalculator.CalculateImageTokensAsync(imagePart.ImageUrl);
                                // Convert tokens back to approximate character count (4 chars per token)
                                totalChars += imageTokens * 4;
                                _logger.LogDebug("Image tokens calculated: {Tokens} (approx {Chars} chars)", 
                                    imageTokens, imageTokens * 4);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to calculate image tokens, using conservative fallback");
                                // Conservative fallback: assume 850 tokens * 4 chars/token = 3400 chars
                                totalChars += 3400;
                            }
                        }
                        else if (part is ProviderContentPart providerPart)
                        {
                            if (providerPart.Type == "text" &&
                                providerPart.ExtensionData?.TryGetValue("text", out var textElement) == true &&
                                textElement.ValueKind == JsonValueKind.String)
                            {
                                totalChars += textElement.GetString()?.Length ?? 0;
                            }
                            else if (providerPart.Type == "image_url")
                            {
                                totalChars += 3400;
                                _logger.LogWarning("Using conservative image token estimate for provider image content");
                            }
                        }
                        else if (part is JsonElement element &&
                                 element.ValueKind == JsonValueKind.Object &&
                                 element.TryGetProperty("type", out var typeElement))
                        {
                            if (typeElement.GetString() == "text" &&
                                element.TryGetProperty("text", out var textElement))
                            {
                                totalChars += textElement.GetString()?.Length ?? 0;
                            }
                            else if (typeElement.GetString() == "image_url")
                            {
                                totalChars += 3400;
                                _logger.LogWarning("Using conservative image token estimate for JSON image content");
                            }
                        }
                    }
                }
                
                // Count name if present
                if (!string.IsNullOrEmpty(message.Name))
                    totalChars += message.Name.Length;
            }
            
            return totalChars;
        }
    }
}
