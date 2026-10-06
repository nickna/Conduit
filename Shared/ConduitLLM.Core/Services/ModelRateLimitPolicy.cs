using System.Text.Json;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Per-model ceilings for one alias or alias prefix.
    /// </summary>
    public sealed class ModelRateLimitRule
    {
        public int? Rpm { get; set; }
        public int? Tpm { get; set; }

        public bool IsEmpty => Rpm is not > 0 && Tpm is not > 0;
    }

    /// <summary>
    /// Resolves a virtual key's per-model rate limit overrides for the alias a request names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching is against the <b>alias the caller sends</b>, not the provider's model id: the
    /// alias is what a caller can control and therefore what an operator is limiting.
    /// </para>
    /// <para>
    /// Precedence is exact alias, then the longest matching prefix rule, then nothing. Longest
    /// prefix rather than first match so that <c>gpt-5-mini*</c> beats <c>gpt-5*</c> regardless
    /// of the order the rules happen to be serialised in.
    /// </para>
    /// </remarks>
    public static class ModelRateLimitPolicy
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Parses the stored override document. Returns null for absent or malformed JSON — a
        /// bad document must not fail requests, it just means no per-model overrides apply.
        /// </summary>
        public static IReadOnlyDictionary<string, ModelRateLimitRule>? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var context = new CoreInternalJsonContext(
                    new JsonSerializerOptions(SerializerOptions));
                var parsed = JsonSerializer.Deserialize(json, context.DictionaryStringModelRateLimitRule);
                return parsed is { Count: > 0 } ? parsed : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds the rule governing <paramref name="modelAlias"/>, or null when none applies —
        /// including when the alias is unknown, in which case only the key and group ceilings do.
        /// </summary>
        public static ModelRateLimitRule? Resolve(
            IReadOnlyDictionary<string, ModelRateLimitRule>? rules,
            string? modelAlias)
        {
            if (rules is null || rules.Count == 0 || string.IsNullOrEmpty(modelAlias))
            {
                return null;
            }

            if (rules.TryGetValue(modelAlias, out var exact))
            {
                return exact.IsEmpty ? null : exact;
            }

            ModelRateLimitRule? best = null;
            var bestPrefixLength = -1;

            foreach (var (pattern, rule) in rules)
            {
                if (pattern.Length < 2 || pattern[^1] != '*')
                {
                    continue;
                }

                var prefix = pattern[..^1];
                if (prefix.Length > bestPrefixLength &&
                    modelAlias.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    best = rule;
                    bestPrefixLength = prefix.Length;
                }
            }

            return best is null || best.IsEmpty ? null : best;
        }
    }
}
