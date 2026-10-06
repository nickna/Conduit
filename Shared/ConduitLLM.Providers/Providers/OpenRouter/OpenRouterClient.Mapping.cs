using System.Text.Json;
using ConduitLLM.Providers.Serialization;

using CoreModels = ConduitLLM.Core.Models;
using ConduitLLM.Providers.Helpers;
using ConduitLLM.Providers.OpenAI;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.OpenRouter
{
    public partial class OpenRouterClient
    {
        protected override void ValidateRequest<TRequest>(TRequest request, string operationName)
        {
            base.ValidateRequest(request, operationName);
            if (request is CoreModels.ChatCompletionRequest chatRequest)
            {
                OpenRouterMultimodalValidator.Validate(chatRequest);
            }
        }

        /// <summary>
        /// Per-mapping provider options (provider/plugins/transforms/models/route), parsed once from
        /// <c>ModelProviderMapping.ProviderOptions</c>. Null when none configured or the JSON is invalid.
        /// </summary>
        private readonly Dictionary<string, JsonElement>? _mappingOptions;

        /// <summary>
        /// Merges the per-mapping provider options into the outgoing request. Precedence:
        /// standard mapped parameters &gt; caller-supplied ExtensionData (merged by the base) &gt; mapping
        /// options — so a per-request choice always beats a per-mapping default, and neither can
        /// clobber a standard parameter. Whole-key merge (no deep merge of nested objects in v1).
        /// </summary>
        protected override object MapToOpenAIRequest(CoreModels.ChatCompletionRequest request)
        {
            var mapped = base.MapToOpenAIRequest(request);
            if (mapped is not IDictionary<string, object?> dict)
            {
                return mapped;
            }

            ApplyPromptCachingIntent(dict, request);

            // Caller session IDs are forwarded verbatim. Inferred affinity is already an opaque
            // HMAC and therefore does not disclose prompt content to OpenRouter.
            if (!dict.ContainsKey("session_id"))
            {
                var sessionId = request.SessionId ?? request.RoutingAffinityKey;
                if (!string.IsNullOrWhiteSpace(sessionId)) dict["session_id"] = sessionId;
            }

            if (_mappingOptions is not null)
            {
                foreach (var (key, value) in _mappingOptions)
                {
                    if (!dict.ContainsKey(key))
                    {
                        dict[key] = value;
                    }
                }
            }

            return mapped;
        }

        private static void ApplyPromptCachingIntent(
            IDictionary<string, object?> mapped,
            CoreModels.ChatCompletionRequest request)
        {
            var intent = request.PromptCachingIntent;
            if (intent is null || mapped.ContainsKey("cache_control")) return;

            var directive = new Dictionary<string, object?> { ["type"] = "ephemeral" };
            if (!string.IsNullOrWhiteSpace(intent.Ttl)) directive["ttl"] = intent.Ttl;

            if (intent.Strategy == CoreModels.PromptCachingStrategy.Automatic)
            {
                if (mapped.TryGetValue("messages", out var automaticMessages) &&
                    automaticMessages is List<OpenAIMessage> existingMessages &&
                    existingMessages.Sum(message =>
                        PromptCacheMarkerInjector.CountMarkers(message.Content, "cache_control")) > 0) return;
                mapped["cache_control"] = directive;
                return;
            }

            if (mapped.TryGetValue("messages", out var messagesObject) &&
                messagesObject is List<OpenAIMessage> messages)
            {
                var targets = PromptCacheMarkerInjector.ResolveTargets(request.Messages, intent.InjectionPoints);
                var added = messages.Sum(message =>
                    PromptCacheMarkerInjector.CountMarkers(message.Content, "cache_control"));
                foreach (var index in targets)
                {
                    if (added >= CoreModels.PromptCachingConstants.MaxExplicitBreakpoints) break;
                    if (PromptCacheMarkerInjector.TryAddMarker(
                        messages[index].Content,
                        "cache_control",
                        directive,
                        out var content))
                    {
                        messages[index] = messages[index].WithContent(content);
                        added++;
                    }
                }
            }
        }

        private Dictionary<string, JsonElement>? ParseProviderOptions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize(
                    json,
                    ProvidersJsonContext.Default.DictionaryStringJsonElement);
                return parsed is { Count: > 0 } ? parsed : null;
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Ignoring malformed OpenRouter ProviderOptions JSON for a mapping.");
                return null;
            }
        }
    }
}
