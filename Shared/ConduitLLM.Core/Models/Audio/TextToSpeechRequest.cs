using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models.Audio
{
    /// <summary>
    /// A text-to-speech synthesis request (OpenAI <c>/audio/speech</c> compatible).
    /// </summary>
    public class TextToSpeechRequest
    {
        /// <summary>The model (alias) to synthesize with.</summary>
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        /// <summary>The text to synthesize.</summary>
        [JsonPropertyName("input")]
        public required string Input { get; set; }

        /// <summary>A stock voice identifier or custom voice reference (provider/model specific).</summary>
        [JsonPropertyName("voice")]
        public required SpeechVoice Voice { get; set; }

        /// <summary>
        /// Output audio format, e.g. <c>mp3</c> or <c>pcm</c>. Defaults to <c>mp3</c> (OpenAI-compatible
        /// default; OpenRouter's own default is pcm, so we normalize to mp3 for stored/played audio).
        /// </summary>
        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; } = "mp3";

        /// <summary>Optional playback speed multiplier (provider support varies).</summary>
        [JsonPropertyName("speed")]
        public double? Speed { get; set; }

        /// <summary>Additional pronunciation or delivery instructions.</summary>
        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }

        /// <summary>Streaming framing format requested by the caller.</summary>
        [JsonPropertyName("stream_format")]
        public string? StreamFormat { get; set; }

        /// <summary>Provider-specific passthrough fields.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}
