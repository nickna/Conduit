using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Source-generated metadata for the highest-volume OpenAI-compatible HTTP contracts.
/// Metadata mode deliberately keeps the caller's naming, converter, and ignore policies
/// authoritative so Gateway wire behavior is unchanged.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(global::ConduitLLM.Core.Models.Audio.TextToSpeechRequest))]
[JsonSerializable(typeof(global::ConduitLLM.Core.Models.Audio.AudioTranscriptionResponse))]
[JsonSerializable(typeof(EmbeddingRequest))]
[JsonSerializable(typeof(ImageGenerationRequest))]
[JsonSerializable(typeof(global::ConduitLLM.Core.Models.Rerank.RerankRequest))]
[JsonSerializable(typeof(global::ConduitLLM.Core.Models.Responses.CreateResponseRequest))]
[JsonSerializable(typeof(VideoGenerationRequest))]
[JsonSerializable(typeof(VideoGenerationResponse))]
[JsonSerializable(typeof(List<ToolCall>))]
[JsonSerializable(typeof(OpenAIErrorResponse))]
[JsonSerializable(typeof(ProviderErrorDetail))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(Dictionary<string, ProviderErrorDetail>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(TextContentPart))]
[JsonSerializable(typeof(ImageUrlContentPart))]
[JsonSerializable(typeof(InputAudioContentPart))]
[JsonSerializable(typeof(FileContentPart))]
[JsonSerializable(typeof(ProviderContentPart))]
[JsonSerializable(typeof(VideoUrlContentPart))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
public partial class CoreHttpJsonContext : JsonSerializerContext;
