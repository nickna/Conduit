using System.Text.Json;
using ConduitLLM.Configuration.Serialization;

namespace ConduitLLM.Configuration.Utilities
{
    /// <summary>
    /// Parses stored JSON metadata documents into dictionaries.
    /// </summary>
    public static class JsonMetadataParser
    {
        /// <summary>
        /// Deserializes a stored metadata document. Malformed JSON is surfaced as null rather
        /// than an error: a diagnostic field must not make the owning resource unreadable.
        /// </summary>
        public static Dictionary<string, JsonElement>? Parse(string? metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata))
                return null;

            try
            {
                return JsonSerializer.Deserialize(
                    metadata,
                    ConfigurationJsonContext.Default.DictionaryStringJsonElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
