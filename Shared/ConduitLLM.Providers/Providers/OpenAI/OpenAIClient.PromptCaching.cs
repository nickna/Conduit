using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Providers.Helpers;

namespace ConduitLLM.Providers.OpenAI;

public partial class OpenAIClient
{
    protected override object MapToOpenAIRequest(ChatCompletionRequest request)
    {
        var mapped = base.MapToOpenAIRequest(request);
        if (mapped is not IDictionary<string, object?> dict) return mapped;

        var intent = request.PromptCachingIntent;
        if (intent is null) return mapped;

        // Caller controls always win, including controls passed through JsonExtensionData.
        if (!dict.ContainsKey("prompt_cache_key") && !string.IsNullOrWhiteSpace(intent.PromptCacheKey))
            dict["prompt_cache_key"] = intent.PromptCacheKey;

        // Older model families reject the v5.6 controls; keep their provider-managed behavior.
        if (!OpenAIPromptCachingAdapter.IsExplicitModel(ProviderModelId)) return mapped;

        if (!dict.ContainsKey("prompt_cache_options"))
        {
            var options = new Dictionary<string, object?>
            {
                ["mode"] = intent.Strategy == PromptCachingStrategy.Explicit ? "explicit" : "implicit"
            };
            if (!string.IsNullOrWhiteSpace(intent.Ttl)) options["ttl"] = intent.Ttl;
            dict["prompt_cache_options"] = options;
        }

        if (intent.Strategy == PromptCachingStrategy.Explicit &&
            dict.TryGetValue("messages", out var messagesObject) &&
            messagesObject is List<OpenAIMessage> messages)
        {
            var targets = PromptCacheMarkerInjector.ResolveTargets(request.Messages, intent.InjectionPoints);
            var added = messages.Sum(message =>
                PromptCacheMarkerInjector.CountMarkers(message.Content, "prompt_cache_breakpoint"));
            foreach (var index in targets)
            {
                if (added >= PromptCachingConstants.MaxExplicitBreakpoints) break;
                if (PromptCacheMarkerInjector.TryAddMarker(
                    messages[index].Content,
                    "prompt_cache_breakpoint",
                    new Dictionary<string, object?> { ["mode"] = "explicit" },
                    out var content))
                {
                    messages[index] = messages[index].WithContent(content);
                    added++;
                }
            }
        }
        return mapped;
    }
}
